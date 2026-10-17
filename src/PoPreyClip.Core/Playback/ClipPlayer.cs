using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using PoPreyClip.Audio;
using PoPreyClip.Config;
using PoPreyClip.Editing;
using PoPreyClip.Recording;

namespace PoPreyClip.Playback;

/// <summary>
/// Preview player for the clip editor, built on the ffmpeg the app already uses, so it plays every codec the recorder
/// writes. One ffmpeg decodes the video (on the GPU when possible) into small BGRA frames; another decodes every audio
/// track side by side, and the tracks are mixed here with live per-track volumes. Nothing runs while paused, and
/// everything stops when the editor closes.
/// </summary>
public sealed class ClipPlayer : IDisposable
{
    private const int FrameSlots = 4;
    private const double MaxPreviewFps = 60;

    private readonly AppConfig _config;
    private readonly ClipMedia _media;
    private readonly object _gate = new();
    private readonly Queue<Frame> _frames = new();
    private readonly Stack<byte[]> _freeBuffers = new();
    private readonly float[] _gains;
    private readonly double _frameStep;
    private Session? _session;
    private int _generation;
    private double _pausedAt;
    private double? _stillTarget;
    private bool _stillRunning;
    private bool _disposed;

    public ClipPlayer(AppConfig config, ClipMedia media, int maxWidth, int maxHeight)
    {
        _config = config;
        _media = media;
        var scale = Math.Min(1, Math.Min(maxWidth / (double)Math.Max(1, media.Width), maxHeight / (double)Math.Max(1, media.Height)));
        FrameWidth = Math.Max(2, (int)(media.Width * scale) & ~1);
        FrameHeight = Math.Max(2, (int)(media.Height * scale) & ~1);
        _frameStep = 1 / Math.Min(media.Fps, MaxPreviewFps);
        _gains = Enumerable.Repeat(1f, media.SourceTracks.Count).ToArray();
    }

    public int FrameWidth { get; }

    public int FrameHeight { get; }

    public int FrameBytes => FrameWidth * FrameHeight * 4;

    public bool IsPlaying => _session is not null;

    /// <summary>Current playback time in seconds (from the start of the clip).</summary>
    public double Position
    {
        get
        {
            var session = _session;
            return session is null ? _pausedAt : session.Clock;
        }
    }

    /// <summary>Changes the volume of a source track right away, also while playing (100 = original).</summary>
    public void SetVolume(int track, int percent)
    {
        if ((uint)track < (uint)_gains.Length)
        {
            _gains[track] = Math.Clamp(percent, 0, 200) / 100f;
        }
    }

    public void Play(double from)
    {
        lock (_gate)
        {
            StopSessionLocked();
            from = Math.Clamp(from, 0, Math.Max(0, _media.Duration - _frameStep));
            _session = new Session(this, from, ++_generation);
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_session is { } session)
            {
                _pausedAt = Math.Min(session.Clock, _media.Duration);
                StopSessionLocked();
                _generation++;
            }
        }
    }

    /// <summary>Decodes the frame at <paramref name="seconds"/> while paused. Rapid calls (dragging) only decode the latest.</summary>
    public void ShowFrameAt(double seconds)
    {
        lock (_gate)
        {
            _pausedAt = Math.Clamp(seconds, 0, _media.Duration);
            _stillTarget = Math.Clamp(seconds, 0, Math.Max(0, _media.Duration - _frameStep));
            if (_stillRunning)
            {
                return;
            }

            _stillRunning = true;
        }

        _ = Task.Run(StillLoop);
    }

    /// <summary>
    /// Hands the frame that should be on screen now to <paramref name="present"/>, if there is a new one. Called by the
    /// UI on every render tick.
    /// </summary>
    public bool PresentFrame(Action<byte[]> present)
    {
        Frame? chosen = null;
        lock (_gate)
        {
            var clock = Position + _frameStep / 2;
            while (_frames.Count > 0 && (_frames.Peek().Time <= clock || _frames.Peek().IsStill))
            {
                if (chosen is not null)
                {
                    RecycleLocked(chosen);
                }

                chosen = _frames.Dequeue();
            }
        }

        if (chosen is null)
        {
            return false;
        }

        try
        {
            present(chosen.Data);
        }
        finally
        {
            lock (_gate)
            {
                RecycleLocked(chosen);
            }
        }

        return true;
    }

    /// <summary>True while a paused frame is still being decoded or waits to be shown.</summary>
    public bool HasPendingFrame
    {
        get
        {
            lock (_gate)
            {
                return _stillRunning || _frames.Count > 0;
            }
        }
    }

    /// <summary>True when the session ran out of video and audio (the end of the clip).</summary>
    public bool ReachedEnd => _session is { Finished: true };

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopSessionLocked();
            _generation++;
            _frames.Clear();
            _freeBuffers.Clear();
        }
    }

    private void StopSessionLocked()
    {
        _session?.Stop();
        _session = null;
        foreach (var frame in _frames)
        {
            frame.Slots?.Release();
        }

        _frames.Clear();
    }

    private void RecycleLocked(Frame frame)
    {
        frame.Slots?.Release();
        if (!_disposed && _freeBuffers.Count < FrameSlots + 1)
        {
            _freeBuffers.Push(frame.Data);
        }
    }

    private byte[] RentBuffer()
    {
        lock (_gate)
        {
            return _freeBuffers.Count > 0 ? _freeBuffers.Pop() : new byte[FrameBytes];
        }
    }

    /// <summary>Queues a decoded frame unless the player moved on (seek, pause) since it was requested.</summary>
    private bool Enqueue(Frame frame, int generation)
    {
        lock (_gate)
        {
            if (generation != _generation || _disposed)
            {
                return false;
            }

            if (frame.IsStill)
            {
                foreach (var old in _frames)
                {
                    RecycleLocked(old);
                }

                _frames.Clear();
            }

            _frames.Enqueue(frame);
            return true;
        }
    }

    private void StillLoop()
    {
        while (true)
        {
            double target;
            int generation;
            lock (_gate)
            {
                if (_stillTarget is null || _disposed)
                {
                    _stillRunning = false;
                    return;
                }

                target = _stillTarget.Value;
                _stillTarget = null;
                generation = _generation;
            }

            var buffer = RentBuffer();
            try
            {
                using var process = StartFfmpeg(
                [
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", Ffmpeg.Format(target), "-i", _media.Path,
                    "-map", "0:v:0", "-frames:v", "1", "-vf", ScaleFilter(), "-f", "rawvideo", "pipe:1",
                ]);
                if (ReadFull(process.StandardOutput.BaseStream, buffer) && Enqueue(new Frame(buffer, target, true, null), generation))
                {
                    buffer = null!;
                }

                process.WaitForExit(2000);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log.Debug($"Editor: quadro em {target:0.00}s não decodificado: {ex.Message}");
            }

            if (buffer is not null)
            {
                lock (_gate)
                {
                    if (_freeBuffers.Count < FrameSlots + 1)
                    {
                        _freeBuffers.Push(buffer);
                    }
                }
            }
        }
    }

    /// <summary>Every source track resampled to 48 kHz stereo float and placed side by side (2 channels per track).</summary>
    internal static string AudioGraph(IReadOnlyList<ClipAudioTrack> tracks)
    {
        var parts = tracks.Select((t, i) =>
            $"[0:a:{t.AudioIndex}]aresample=48000,aformat=sample_fmts=flt:channel_layouts=stereo[s{i}]").ToList();
        if (tracks.Count == 1)
        {
            parts[0] = parts[0][..parts[0].LastIndexOf('[')] + "[a]";
        }
        else
        {
            parts.Add($"{string.Concat(Enumerable.Range(0, tracks.Count).Select(i => $"[s{i}]"))}amerge=inputs={tracks.Count}[a]");
        }

        return string.Join(';', parts);
    }

    private string ScaleFilter() =>
        (_media.Fps > MaxPreviewFps + 0.5 ? $"fps={MaxPreviewFps},":"") + $"scale={FrameWidth}:{FrameHeight}:flags=bilinear,format=bgra";

    private static bool ReadFull(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private static Process StartFfmpeg(IReadOnlyList<string> args, string ffmpeg)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in args)
        {
            info.ArgumentList.Add(argument);
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível iniciar o ffmpeg.");
        PoPreyClip.Native.ChildProcessJob.Attach(process);
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Log.Debug($"Editor ffmpeg: {e.Data}");
            }
        };
        process.BeginErrorReadLine();
        return process;
    }

    private Process StartFfmpeg(IReadOnlyList<string> args) => StartFfmpeg(args, _config.FfmpegExe);

    private sealed record Frame(byte[] Data, double Time, bool IsStill, SemaphoreSlim? Slots);

    /// <summary>One continuous run of playback from a start time. Pausing or seeking ends it and starts a new one.</summary>
    private sealed class Session
    {
        private readonly ClipPlayer _owner;
        private readonly double _start;
        private readonly int _generation;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _slots = new(FrameSlots, FrameSlots);
        private readonly Stopwatch _wallClock = new();
        private readonly Process _video;
        private readonly NamedPipeServerStream _videoPipe;
        private readonly Process? _audio;
        private readonly AudioRenderer? _renderer;
        private readonly PcmFifo? _fifo;
        private readonly int _trackCount;
        private volatile bool _firstFrame;
        private volatile bool _videoEnded;
        private volatile bool _audioEnded;
        private float[] _mixInput = [];
        private readonly PeakLimiter _limiter = new();

        public Session(ClipPlayer owner, double start, int generation)
        {
            _owner = owner;
            _start = start;
            _generation = generation;
            var media = owner._media;
            // Frames go through a named pipe with room for two of them: the standard output pipe moves only 4 KB per
            // read, which made reading the frames cost more than decoding them.
            var pipeName = $"popreyclip-editor-{Environment.ProcessId}-{Guid.NewGuid():N}";
            _videoPipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None, owner.FrameBytes * 2, 0);
            _video = owner.StartFfmpeg(
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-hwaccel", "d3d11va", "-ss", Ffmpeg.Format(start), "-i", media.Path,
                "-map", "0:v:0", "-an", "-sn", "-vf", owner.ScaleFilter(), "-f", "rawvideo", "-y", $@"\\.\pipe\{pipeName}",
            ]);
            new Thread(ReadVideo) { IsBackground = true, Name = "Editor vídeo" }.Start();

            _trackCount = media.SourceTracks.Count;
            if (_trackCount > 0)
            {
                // About 2 s of every track side by side: enough to ride out a slow disk without using much memory.
                _fifo = new PcmFifo(AudioFormat.SampleRate * 2 * _trackCount * sizeof(float) * 2);
                _audio = owner.StartFfmpeg(
                [
                    "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", Ffmpeg.Format(start), "-i", media.Path,
                    "-filter_complex", AudioGraph(media.SourceTracks), "-map", "[a]", "-f", "f32le", "pipe:1",
                ]);
                new Thread(ReadAudio) { IsBackground = true, Name = "Editor leitura de áudio" }.Start();
                _renderer = new AudioRenderer(Mix, () => _firstFrame && _fifo.Available >= AudioFormat.SampleRate / 10 * 2 * _trackCount * sizeof(float));
            }
            else
            {
                _wallClock.Start();
            }
        }

        public double Clock
        {
            get
            {
                if (_renderer is { Faulted: false } renderer)
                {
                    return _start + renderer.PlayedSeconds;
                }

                if (!_wallClock.IsRunning && _firstFrame)
                {
                    _wallClock.Start();
                }

                return _start + _wallClock.Elapsed.TotalSeconds;
            }
        }

        public bool Finished => _videoEnded && (_audio is null || _audioEnded) && _owner._frames.Count == 0;

        public void Stop()
        {
            _stop.Cancel();
            _renderer?.Dispose();
            Kill(_video);
            _videoPipe.Dispose();
            if (_audio is not null)
            {
                Kill(_audio);
            }
        }

        private static void Kill(Process process)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already finished.
            }

            process.Dispose();
        }

        private void ReadVideo()
        {
            var index = 0;
            try
            {
                var stream = _videoPipe;
                stream.WaitForConnection();
                while (!_stop.IsCancellationRequested)
                {
                    _slots.Wait(_stop.Token);
                    var buffer = _owner.RentBuffer();
                    if (!ReadFull(stream, buffer))
                    {
                        _slots.Release();
                        break;
                    }

                    if (!_owner.Enqueue(new Frame(buffer, _start + index * _owner._frameStep, false, _slots), _generation))
                    {
                        _slots.Release();
                        break;
                    }

                    index++;
                    _firstFrame = true;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
            finally
            {
                _videoEnded = true;
                // A clip without video frames left (or none at all) must not hold the audio back.
                _firstFrame = true;
            }
        }

        private void ReadAudio()
        {
            var fifo = _fifo!;
            var chunk = new byte[AudioFormat.SampleRate / 50 * 2 * _trackCount * sizeof(float)];
            try
            {
                var stream = _audio!.StandardOutput.BaseStream;
                while (!_stop.IsCancellationRequested)
                {
                    var n = stream.Read(chunk, 0, chunk.Length);
                    if (n == 0)
                    {
                        break;
                    }

                    // Waits for room instead of dropping audio; ffmpeg simply pauses while the pipe is full.
                    while (fifo.Capacity - fifo.Available < n && !_stop.IsCancellationRequested)
                    {
                        Thread.Sleep(10);
                    }

                    fifo.Write(chunk.AsSpan(0, n));
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
            finally
            {
                _audioEnded = true;
            }
        }

        /// <summary>Mixes the side-by-side tracks into stereo with the current volumes.</summary>
        private int Mix(Span<float> stereo, int frames)
        {
            var fifo = _fifo!;
            var inputChannels = 2 * _trackCount;
            var bytesPerFrame = inputChannels * sizeof(float);
            var available = Math.Min(frames, fifo.Available / bytesPerFrame);
            if (available == 0)
            {
                if (_audioEnded && _renderer is not null)
                {
                    _renderer.SourceEnded = true;
                }

                return 0;
            }

            if (_mixInput.Length < available * inputChannels)
            {
                _mixInput = new float[frames * inputChannels];
            }

            var input = _mixInput.AsSpan(0, available * inputChannels);
            fifo.Read(MemoryMarshal.AsBytes(input));
            var gains = _owner._gains;
            for (var f = 0; f < available; f++)
            {
                float left = 0, right = 0;
                var row = f * inputChannels;
                for (var t = 0; t < _trackCount; t++)
                {
                    var gain = gains[t];
                    left += input[row + 2 * t] * gain;
                    right += input[row + 2 * t + 1] * gain;
                }

                // The limiter works in 16-bit units, like in the recorder.
                stereo[2 * f] = left * 32767f;
                stereo[2 * f + 1] = right * 32767f;
            }

            var output = stereo[..(available * 2)];
            _limiter.Process(output);
            for (var i = 0; i < output.Length; i++)
            {
                output[i] /= 32767f;
            }

            return available;
        }
    }
}
