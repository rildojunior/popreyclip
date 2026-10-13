using System.Runtime.InteropServices;
using PoPreyClip.Audio.Interop;
using static PoPreyClip.Audio.Interop.CoreAudioNative;

namespace PoPreyClip.Audio;

/// <summary>Event-driven WASAPI capture that pushes 48 kHz stereo PCM into a <see cref="PcmFifo"/>.</summary>
internal sealed class WasapiCapture : IDisposable
{
    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly AutoResetEvent _packetReady = new(false);
    private readonly PcmFifo _fifo;
    private readonly Thread _thread;
    private volatile bool _running;

    public WasapiCapture(IAudioClient client, bool processLoopback, PcmFifo fifo, string description)
    {
        _client = client;
        _fifo = fifo;
        Description = description;

        var format = AudioFormat.ToWaveFormat();
        uint flags;
        long bufferDuration;
        if (processLoopback)
        {
            // Process loopback converts to the requested format itself.
            flags = StreamFlagsLoopback | StreamFlagsEventCallback;
            bufferDuration = 200_000; // 20 ms, in 100 ns units
        }
        else
        {
            flags = StreamFlagsEventCallback | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality;
            bufferDuration = 1_000_000; // 100 ms
        }

        Check(_client.Initialize(0, flags, bufferDuration, 0, ref format, IntPtr.Zero), "IAudioClient.Initialize");
        Check(_client.SetEventHandle(_packetReady.SafeWaitHandle.DangerousGetHandle()), "IAudioClient.SetEventHandle");
        var iid = IidAudioCaptureClient;
        Check(_client.GetService(ref iid, out var service), "IAudioClient.GetService");
        _capture = (IAudioCaptureClient)service;

        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = $"Captura {description}", Priority = ThreadPriority.AboveNormal };
    }

    public string Description { get; }

    public bool Faulted { get; private set; }

    public void Start()
    {
        Check(_client.Start(), "IAudioClient.Start");
        _running = true;
        _thread.Start();
    }

    private unsafe void CaptureLoop()
    {
        try
        {
            while (_running)
            {
                _packetReady.WaitOne(100);
                while (_running)
                {
                    Check(_capture.GetNextPacketSize(out var packetFrames), "GetNextPacketSize");
                    if (packetFrames == 0)
                    {
                        break;
                    }

                    Check(_capture.GetBuffer(out var data, out var frames, out var bufferFlags, out _, out _), "GetBuffer");
                    var bytes = checked((int)frames * AudioFormat.BytesPerFrame);
                    if ((bufferFlags & BufferFlagsSilent) != 0 || data == IntPtr.Zero)
                    {
                        _fifo.WriteSilence(bytes);
                    }
                    else
                    {
                        _fifo.Write(new ReadOnlySpan<byte>((void*)data, bytes));
                    }

                    Check(_capture.ReleaseBuffer(frames), "ReleaseBuffer");
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or OverflowException)
        {
            Faulted = true;
            Log.Warn($"Captura de áudio interrompida ({Description}): {ex.Message}");
        }
    }

    public void Dispose()
    {
        // The audio client lives in the multithreaded COM apartment and has no proxy for single-threaded
        // ones. When the WPF UI thread (STA) stops recording, for example on exit, using the object there
        // fails with InvalidCastException, so the release always runs on a thread-pool (MTA) thread.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            Task.Run(DisposeCore).GetAwaiter().GetResult();
        }
        else
        {
            DisposeCore();
        }
    }

    private void DisposeCore()
    {
        _running = false;
        if (_thread.IsAlive)
        {
            _thread.Join(1000);
        }

        try
        {
            _client.Stop();
        }
        catch (COMException)
        {
            // Device already gone (unplugged headset, for example).
        }

        Marshal.ReleaseComObject(_capture);
        Marshal.ReleaseComObject(_client);
        _packetReady.Dispose();
    }
}
