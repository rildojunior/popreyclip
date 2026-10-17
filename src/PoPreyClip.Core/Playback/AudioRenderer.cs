using System.Diagnostics;
using System.Runtime.InteropServices;
using PoPreyClip.Audio;
using PoPreyClip.Audio.Interop;
using static PoPreyClip.Audio.Interop.CoreAudioNative;

namespace PoPreyClip.Playback;

/// <summary>
/// Plays 48 kHz stereo float audio on the default output device through shared-mode WASAPI. A callback fills each
/// buffer and says how many frames were real audio; the rest is silence. The played time of the real audio is the
/// clock the video follows.
/// </summary>
internal sealed class AudioRenderer : IDisposable
{
    /// <summary>Fills interleaved stereo frames and returns how many of them are real audio (the rest stays silent).</summary>
    public delegate int FillCallback(Span<float> stereo, int frames);

    private const int Channels = 2;
    private const long BufferDuration = 1_000_000; // 100 ms, in 100 ns units

    private readonly FillCallback _fill;
    private readonly Func<bool> _ready;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private long _playedFrames;
    private long _updatedTicks;

    /// <param name="ready">Playback waits until this returns true (enough audio and video buffered), up to one second.</param>
    public AudioRenderer(FillCallback fill, Func<bool> ready)
    {
        _fill = fill;
        _ready = ready;
        // A new thread is in the multithreaded COM apartment, which WASAPI objects need.
        _thread = new Thread(Run) { IsBackground = true, Name = "Editor áudio", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>True once the device started playing.</summary>
    public bool Started { get; private set; }

    public bool Faulted { get; private set; }

    /// <summary>Set by the owner when no more audio will come, so the clock keeps running past the end of the audio.</summary>
    public bool SourceEnded { get; set; }

    /// <summary>Seconds of real audio heard so far, smoothed between device updates.</summary>
    public double PlayedSeconds
    {
        get
        {
            var played = Interlocked.Read(ref _playedFrames) / (double)AudioFormat.SampleRate;
            var updated = Interlocked.Read(ref _updatedTicks);
            if (updated == 0)
            {
                return played;
            }

            var since = Stopwatch.GetElapsedTime(updated).TotalSeconds;
            // Between device callbacks time moves on smoothly; after the audio ends the clock runs freely.
            return played + (SourceEnded ? since : Math.Min(since, 0.03));
        }
    }

    private unsafe void Run()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioRenderClient? render = null;
        using var bufferReady = new AutoResetEvent(false);
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device), "GetDefaultAudioEndpoint");
            var iid = IidAudioClient;
            Check(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var instance), "IMMDevice.Activate");
            client = (IAudioClient)instance;
            var format = new WaveFormatEx
            {
                FormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
                Channels = Channels,
                SamplesPerSec = AudioFormat.SampleRate,
                BitsPerSample = 32,
                BlockAlign = Channels * 4,
                AvgBytesPerSec = AudioFormat.SampleRate * Channels * 4,
            };
            Check(client.Initialize(0, StreamFlagsEventCallback | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality,
                BufferDuration, 0, ref format, IntPtr.Zero), "IAudioClient.Initialize");
            Check(client.SetEventHandle(bufferReady.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            Check(client.GetBufferSize(out var bufferFrames), "GetBufferSize");
            var renderIid = IidAudioRenderClient;
            Check(client.GetService(ref renderIid, out var service), "GetService");
            render = (IAudioRenderClient)service;

            var waited = Stopwatch.StartNew();
            while (_running && !_ready() && waited.ElapsedMilliseconds < 1000)
            {
                Thread.Sleep(5);
            }

            long realWritten = 0;
            var first = true;
            while (_running)
            {
                if (!first)
                {
                    bufferReady.WaitOne(200);
                }

                Check(client.GetCurrentPadding(out var padding), "GetCurrentPadding");
                var free = (int)(bufferFrames - padding);
                if (free > 0)
                {
                    Check(render.GetBuffer((uint)free, out var data), "GetBuffer");
                    var span = new Span<float>((void*)data, free * Channels);
                    span.Clear();
                    var real = _fill(span, free);
                    Check(render.ReleaseBuffer((uint)free, 0), "ReleaseBuffer");
                    realWritten += real;
                    padding += (uint)free;
                }

                if (first)
                {
                    Check(client.Start(), "IAudioClient.Start");
                    Started = true;
                    first = false;
                }

                // Frames still waiting in the device buffer have not been heard yet.
                Interlocked.Exchange(ref _playedFrames, Math.Max(0, realWritten - padding));
                Interlocked.Exchange(ref _updatedTicks, Stopwatch.GetTimestamp());
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Faulted = true;
            Log.Warn($"Editor: não foi possível tocar o áudio: {ex.Message}");
        }
        finally
        {
            try
            {
                client?.Stop();
            }
            catch (COMException)
            {
            }

            if (render is not null)
            {
                Marshal.ReleaseComObject(render);
            }

            if (client is not null)
            {
                Marshal.ReleaseComObject(client);
            }

            if (device is not null)
            {
                Marshal.ReleaseComObject(device);
            }

            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(1000);
    }
}
