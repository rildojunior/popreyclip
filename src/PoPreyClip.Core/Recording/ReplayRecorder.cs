using System.Diagnostics;
using System.Globalization;
using PoPreyClip.Audio;
using PoPreyClip.Config;
using PoPreyClip.Native;

namespace PoPreyClip.Recording;

/// <summary>
/// Continuous replay buffer made of two ffmpeg processes that write short MPEG-TS segments:
/// <list type="bullet">
/// <item>audio: one raw PCM named pipe per track; its timestamps count samples from the first one;</item>
/// <item>video: the game window (Windows Graphics Capture) or the monitor (Desktop Duplication), stamped with
/// the wall clock and shifted so that zero is the audio's first sample.</item>
/// </list>
/// Keeping them apart means a capture that stalls (minimized game, GPU at 100%) can never hold up the audio:
/// in one process ffmpeg stops reading every input that runs ahead of the slowest one, so a frozen picture
/// used to freeze the sound too and push it out of sync. Clips join both on the shared time base.
/// Old segments are deleted, so only the last few seconds stay on disk until a clip is saved.
/// </summary>
internal sealed class ReplayRecorder : IDisposable
{
    public const string VideoSegmentPrefix = "v";
    public const string AudioSegmentPrefix = "a";
    private static int _sessionCounter;
    private readonly AppConfig _config;
    private readonly Func<AppConfig> _live;
    private readonly List<(Process Process, StreamWriter Log)> _processes = [];
    private long _frames;
    private long _lastFrameTick = Environment.TickCount64;

    /// <param name="config">Settings fixed for this recording (video, tracks).</param>
    /// <param name="source">What to film.</param>
    /// <param name="live">Current settings, for values that may change while recording (clip length).</param>
    public ReplayRecorder(AppConfig config, VideoSource source, Func<AppConfig>? live = null)
    {
        _config = config;
        Source = source;
        _live = live ?? (() => config);
        BufferDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoPreyClip", "buffer",
            $"{Environment.ProcessId}-{Interlocked.Increment(ref _sessionCounter)}");
    }

    public string BufferDirectory { get; }

    public VideoSource Source { get; }

    public bool HasAudio { get; private set; }

    /// <summary>Seconds since ffmpeg last encoded a new video frame (or since start, before the first one).</summary>
    public double SecondsSinceLastFrame => (Environment.TickCount64 - Interlocked.Read(ref _lastFrameTick)) / 1000.0;

    /// <summary>Deletes buffers left behind by PoPreyClip instances that are no longer running.</summary>
    public static void CleanupStaleBuffers()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoPreyClip", "buffer");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            var pidText = name.Split('-')[0];
            if (int.TryParse(pidText, out var pid) && (pid == Environment.ProcessId || IsPoPreyClipRunning(pid)))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug($"Buffer antigo em uso, mantido: {directory}");
            }
        }
    }

    private static bool IsPoPreyClipRunning(int pid)
    {
        var path = PoPreyClip.Native.Win32.GetProcessPath(pid);
        return path is not null && Path.GetFileNameWithoutExtension(path).StartsWith("PoPreyClip", StringComparison.OrdinalIgnoreCase);
    }

    public bool HasExited => _processes.Any(p => p.Process.HasExited);

    public int ExitCode => _processes.FirstOrDefault(p => p.Process.HasExited).Process?.ExitCode ?? 0;

    /// <summary>True when the video process (always started last) ended while the audio one still runs.</summary>
    public bool VideoExited => _processes.Count > 0 && _processes[^1].Process.HasExited
        && _processes.SkipLast(1).All(p => !p.Process.HasExited);

    /// <summary>
    /// Starts the audio process, then <paramref name="startPacer"/> (audio flows into the pipes), waits for the
    /// first sample and starts the video process aligned to it.
    /// </summary>
    public void Start(IReadOnlyList<PipeAudioSink> audioTracks, Action startPacer)
    {
        if (Directory.Exists(BufferDirectory))
        {
            Directory.Delete(BufferDirectory, recursive: true);
        }

        Directory.CreateDirectory(BufferDirectory);
        HasAudio = audioTracks.Count > 0;
        if (HasAudio)
        {
            Launch("audio", BuildAudioArguments(_config, audioTracks.Select(t => t.PipePath).ToList()), video: false);
        }

        startPacer();
        var timeZero = DateTimeOffset.UtcNow;
        if (HasAudio)
        {
            try
            {
                if (audioTracks[0].FirstSample.Wait(TimeSpan.FromSeconds(10)))
                {
                    timeZero = audioTracks[0].FirstSample.Result;
                }
                else
                {
                    Log.Error($"O FFmpeg não começou a gravar o áudio: {ReadFfmpegLogTail()}");
                    throw new InvalidOperationException("O FFmpeg não começou a gravar o áudio.");
                }
            }
            catch (AggregateException ex) when (ex.InnerException is TaskCanceledException)
            {
                throw new InvalidOperationException("A gravação foi interrompida ao iniciar.");
            }
        }

        var encoder = EncoderSelector.Resolve(_config);
        var gpuFrames = EncoderSelector.TakesGpuFrames(_config, encoder);
        var size = Source.IsWindow ? Win32.GetClientSize(Source.Window) : Win32.GetMonitorSize(_config.Video.MonitorIndex);
        var output = OutputSize(_config.Video.Resolution, size);
        Log.Info(output is { } o
            ? $"Vídeo: {size.Width}x{size.Height} reduzido para {o.Width}x{o.Height}, {_config.Video.Fps} FPS, {_config.Video.BitrateMbps} Mbps."
            : $"Vídeo: {size.Width}x{size.Height} no tamanho original, {_config.Video.Fps} FPS, {_config.Video.BitrateMbps} Mbps.");
        Launch("video", BuildVideoArguments(_config, encoder, Source, timeZero, gpuFrames, size), video: true);
    }

    private void Launch(string name, IReadOnlyList<string> arguments, bool video)
    {
        var info = new ProcessStartInfo(_config.FfmpegExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = BufferDirectory,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Log.Debug($"ffmpeg ({name}) " + string.Join(' ', info.ArgumentList));
        var log = new StreamWriter(Path.Combine(BufferDirectory, $"ffmpeg-{name}.log")) { AutoFlush = true };
        var process = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível iniciar o ffmpeg.");
        _processes.Add((process, log));
        PoPreyClip.Native.ChildProcessJob.Attach(process);
        PoPreyClip.Native.RecorderPriority.Raise(process, gpu: video);
        if (video)
        {
            process.OutputDataReceived += (_, e) => OnProgress(e.Data);
        }

        process.BeginOutputReadLine();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (log)
                {
                    log.WriteLine(e.Data);
                }
            }
        };
        process.BeginErrorReadLine();
    }

    /// <summary>ffmpeg -progress lines ("frame=123") tell whether video frames are still arriving.</summary>
    private void OnProgress(string? line)
    {
        if (line is null || !line.StartsWith("frame=", StringComparison.Ordinal)
            || !long.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames))
        {
            return;
        }

        if (frames > Interlocked.Exchange(ref _frames, frames))
        {
            Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount64);
        }
    }

    /// <summary>
    /// Size the clip is recorded at: the source shrunk to <paramref name="height"/> keeping its shape, or null when the
    /// source is already that small (it is never enlarged) or its size is unknown.
    /// </summary>
    internal static (int Width, int Height)? OutputSize(int height, (int Width, int Height) source)
    {
        if (source.Width <= 0 || source.Height <= 0 || source.Height <= height)
        {
            return null;
        }

        var width = (int)Math.Round(source.Width * (double)height / source.Height / 2) * 2;
        return (Math.Max(2, width), height / 2 * 2);
    }

    /// <param name="gpuFrames">AMD AMF takes the captured frames straight from the video card (NVENC always does).</param>
    /// <param name="sourceSize">Size of the window or monitor being filmed, used to shrink it to the chosen resolution.</param>
    internal static List<string> BuildVideoArguments(AppConfig config, string? encoderOverride, VideoSource source, DateTimeOffset timeZero,
        bool gpuFrames = false, (int Width, int Height) sourceSize = default)
    {
        var video = config.Video;
        var encoder = encoderOverride ?? (string.Equals(video.Encoder, EncoderSelector.Auto, StringComparison.OrdinalIgnoreCase) ? "libx264" : video.Encoder);
        var isNvenc = encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase);
        var isAmf = encoder.EndsWith("_amf", StringComparison.OrdinalIgnoreCase);
        // Target bitrate, with room for busy scenes (peak 1.5x) and a 2 s buffer.
        var bitrate = Kbps(video.BitrateMbps * 1000);
        var maxrate = Kbps(video.BitrateMbps * 1500);
        var bufsize = Kbps(video.BitrateMbps * 2000);
        // A keyframe starts every segment, so a clip can begin at any of them.
        var gop = (video.Fps * video.SegmentSeconds).ToString(CultureInfo.InvariantCulture);
        var fps = video.Fps.ToString(CultureInfo.InvariantCulture);
        var cursor = video.ShowCursor ? 1 : 0;
        var output = OutputSize(video.Resolution, sourceSize);

        // Window capture films only the game (no alt-tab, overlays or desktop). Frames arrive when the game draws;
        // width/height -2 keep the size even, which the encoders need. To record smaller, the capture itself shrinks the
        // picture on the video card (scale_aspect), which costs nothing extra.
        var input = source.IsWindow
            ? $"gfxcapture=hwnd={(ulong)source.Window}:max_framerate={fps}:capture_cursor={cursor}:" + (output is { } size
                ? $"width={size.Width}:height={size.Height}:resize_mode=scale_aspect"
                : "width=-2:height=-2")
            : $"ddagrab=output_idx={video.MonitorIndex}:framerate={fps}:draw_mouse={cursor}";

        // Wall-clock timestamps (seconds since 1970) shifted at the input so 0 = the audio's first sample, and kept
        // as they are (-copyts). Shifting at the input keeps them small, which the constant frame rate needs.
        var offset = (timeZero.ToUnixTimeMilliseconds() / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-stats_period", "1", "-y",
            "-use_wallclock_as_timestamps", "1", "-itsoffset", "-" + offset, "-f", "lavfi", "-i", input,
            "-copyts",
            "-map", "0:v",
            // Constant frame rate: when frames stop (minimized game) the gap is filled with the last picture.
            "-fps_mode", "cfr", "-r", fps,
        };

        // The capture hands over D3D11 frames. An encoder that can't read them gets a copy in system memory. The monitor
        // capture can't be shrunk on the video card with this FFmpeg (scale_d3d11 refuses its frames), so it is shrunk
        // on that copy; shrinking there is cheaper than copying the full-size picture.
        var shrinkMonitor = !source.IsWindow && output is not null;
        if (shrinkMonitor || (!isNvenc && !(isAmf && gpuFrames)))
        {
            var format = encoder.Equals("libx264", StringComparison.OrdinalIgnoreCase) ? "yuv420p" : "nv12";
            var scale = shrinkMonitor ? $"scale=w={output!.Value.Width}:h={output.Value.Height}:flags=fast_bilinear," : "";
            args.AddRange(["-vf", $"hwdownload,format=bgra,{scale}format={format}"]);
        }

        if (isNvenc)
        {
            args.AddRange(["-c:v", encoder, "-preset", "p4", "-tune", "hq", "-rc", "vbr",
                "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize, "-forced-idr", "1"]);
        }
        else if (isAmf)
        {
            // Every keyframe is an IDR with the stream headers, so each segment plays on its own.
            args.AddRange(["-c:v", encoder, "-usage", "transcoding", "-quality", "balanced", "-rc", "vbr_peak",
                "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize, "-bf", "0", "-forced_idr", "1", "-header_spacing", gop]);
        }
        else if (encoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["-c:v", encoder, "-preset", "veryfast", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize,
                "-forced_idr", "1"]);
        }
        else
        {
            args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize]);
        }

        args.AddRange(["-g", gop]);
        args.AddRange(SegmentArguments(video, VideoSegmentPrefix));
        return args;
    }

    private static string Kbps(int kbps) => kbps.ToString(CultureInfo.InvariantCulture) + "k";

    internal static List<string> BuildAudioArguments(AppConfig config, IReadOnlyList<string> audioPipes)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-y" };
        foreach (var pipe in audioPipes)
        {
            args.AddRange([
                "-thread_queue_size", "4096",
                "-f", AudioFormat.FfmpegSampleFormat, "-ar", AudioFormat.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", AudioFormat.Channels.ToString(CultureInfo.InvariantCulture), "-i", pipe,
            ]);
        }

        for (var i = 0; i < audioPipes.Count; i++)
        {
            args.AddRange(["-map", $"{i}:a"]);
        }

        args.AddRange(["-c:a", "aac", "-b:a", $"{config.Audio.AudioBitrateKbps}k"]);
        args.AddRange(SegmentArguments(config.Video, AudioSegmentPrefix));
        return args;
    }

    /// <summary>Segment muxer settings. flush_packets writes every packet right away, so a segment never ends mid-frame.</summary>
    private static string[] SegmentArguments(VideoConfig video, string prefix) =>
    [
        "-max_muxing_queue_size", "2048",
        "-f", "segment", "-segment_time", video.SegmentSeconds.ToString(CultureInfo.InvariantCulture),
        "-segment_format", "mpegts", "-segment_format_options", "flush_packets=1", "-reset_timestamps", "0",
        prefix + "%06d.ts",
    ];

    /// <summary>
    /// Waits until the video segment being written right now is finished (ffmpeg moved on to the next one), so a
    /// clip includes the moment the key was pressed. Returns the finished segment, or null if it did not finish in
    /// time (e.g. a minimized game sends no frames): then the growing segment is used as it is.
    /// </summary>
    public async Task<string?> WaitForCurrentSegmentAsync()
    {
        var current = NewestSegment(VideoSegmentPrefix);
        if (current is null)
        {
            return null;
        }

        var deadline = Environment.TickCount64 + (_config.Video.SegmentSeconds * 1000) + 1500;
        while (Environment.TickCount64 < deadline && !HasExited)
        {
            if (string.CompareOrdinal(NewestSegment(VideoSegmentPrefix), current) > 0)
            {
                return current;
            }

            await Task.Delay(100);
        }

        return null;
    }

    private string? NewestSegment(string prefix) => Segments(prefix).LastOrDefault();

    private List<string> Segments(string prefix) =>
        Directory.GetFiles(BufferDirectory, prefix + "*.ts").Order(StringComparer.Ordinal).ToList();

    /// <summary>Newest non-empty segments of one stream covering at least <paramref name="seconds"/>, ending at <paramref name="last"/> when given.</summary>
    public List<string> GetRecentSegments(string prefix, int seconds, string? last = null)
    {
        var needed = (int)Math.Ceiling(seconds / (double)_config.Video.SegmentSeconds) + 2;
        return Segments(prefix)
            .Where(f => last is null || string.CompareOrdinal(Path.GetFileName(f), Path.GetFileName(last)) <= 0)
            .TakeLast(needed)
            .Where(f => new FileInfo(f).Length > 0)
            .ToList();
    }

    /// <summary>Deletes segments that can no longer be part of a clip.</summary>
    public void Cleanup()
    {
        if (!Directory.Exists(BufferDirectory))
        {
            return;
        }

        var keep = (int)Math.Ceiling(Math.Max(_config.ClipSeconds, _live().ClipSeconds) / (double)_config.Video.SegmentSeconds) + 4;
        foreach (var prefix in new[] { VideoSegmentPrefix, AudioSegmentPrefix })
        {
            var segments = Segments(prefix);
            foreach (var old in segments.Take(Math.Max(0, segments.Count - keep)))
            {
                try
                {
                    File.Delete(old);
                }
                catch (IOException)
                {
                    // Still being read by an export; try again on the next cleanup.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public string ReadFfmpegLogTail()
    {
        var lines = new List<string>();
        foreach (var file in Directory.Exists(BufferDirectory) ? Directory.GetFiles(BufferDirectory, "ffmpeg-*.log") : [])
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                lines.AddRange(reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(5));
            }
            catch (IOException)
            {
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    public void Dispose()
    {
        // Video first ("q" finishes the segment); the audio process ends by itself when the pipes close.
        foreach (var (process, _) in Enumerable.Reverse(_processes))
        {
            if (process.HasExited)
            {
                continue;
            }

            try
            {
                process.StandardInput.Write('q');
                process.StandardInput.Flush();
            }
            catch (IOException)
            {
            }
        }

        foreach (var (process, log) in _processes)
        {
            if (!process.HasExited && !process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
            lock (log)
            {
                log.Dispose();
            }
        }

        try
        {
            Directory.Delete(BufferDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
