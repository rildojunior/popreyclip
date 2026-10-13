using System.Diagnostics;
using PoPreyClip.Config;

namespace PoPreyClip.Audio;

public interface IAudioSink
{
    void Write(ReadOnlySpan<byte> pcm);
}

/// <summary>
/// Clock-driven mixer. Every 10 ms it pulls exactly the number of frames that real time requires
/// from each source, pads gaps with silence (process loopback sends nothing while an app is silent),
/// applies live per-source volume, and writes every output track. This keeps all tracks the same
/// length and aligned with each other.
/// </summary>
internal sealed class AudioPacer : IDisposable
{
    private const int MaxChunkFrames = AudioFormat.SampleRate / 10;
    private const int MaxLatencyBytes = AudioFormat.SampleRate * AudioFormat.BytesPerFrame * 150 / 1000;

    private readonly IReadOnlyList<PcmFifo> _sources;
    private readonly IReadOnlyList<string> _sourceIds;
    private readonly IReadOnlyList<AudioTrackSpec> _tracks;
    private readonly IReadOnlyList<IAudioSink> _sinks;
    private readonly Func<AppConfig> _config;
    private readonly Thread _thread;
    private volatile bool _running;
    private readonly float[] _levels;

    public AudioPacer(
        IReadOnlyList<PcmFifo> sources,
        IReadOnlyList<string> sourceIds,
        IReadOnlyList<AudioTrackSpec> tracks,
        IReadOnlyList<IAudioSink> sinks,
        Func<AppConfig> config)
    {
        _sources = sources;
        _sourceIds = sourceIds;
        _tracks = tracks;
        _sinks = sinks;
        _config = config;
        _levels = new float[sources.Count];
        _thread = new Thread(Run) { IsBackground = true, Name = "PoPreyClip audio pacer", Priority = ThreadPriority.Highest };
    }

    public void Start()
    {
        _running = true;
        _thread.Start();
    }

    private void Run()
    {
        var sourceBuffers = _sources.Select(_ => new byte[MaxChunkFrames * AudioFormat.BytesPerFrame]).ToArray();
        var gains = new float[_sources.Count];
        var accumulator = new float[MaxChunkFrames * AudioFormat.Channels];
        var output = new byte[MaxChunkFrames * AudioFormat.BytesPerFrame];
        var limiters = _tracks.Select(_ => new PeakLimiter()).ToArray();
        var clock = Stopwatch.StartNew();
        long framesWritten = 0;

        while (_running)
        {
            var due = clock.Elapsed.Ticks * AudioFormat.SampleRate / TimeSpan.TicksPerSecond - framesWritten;
            if (due <= 0)
            {
                Thread.Sleep(5);
                continue;
            }

            var frames = (int)Math.Min(due, MaxChunkFrames);
            var bytes = frames * AudioFormat.BytesPerFrame;
            UpdateGains(gains);

            for (var s = 0; s < _sources.Count; s++)
            {
                var fifo = _sources[s];
                var excess = fifo.Available - bytes - MaxLatencyBytes;
                if (excess > 0)
                {
                    // The device clock ran ahead of ours: drop the oldest audio to keep latency bounded.
                    fifo.Drop(excess - excess % AudioFormat.BytesPerFrame);
                }

                var read = fifo.Read(sourceBuffers[s].AsSpan(0, bytes));
                sourceBuffers[s].AsSpan(read, bytes - read).Clear();
                var level = AudioMath.Rms(sourceBuffers[s].AsSpan(0, bytes)) * gains[s];
                if (level > Volatile.Read(ref _levels[s]))
                {
                    Volatile.Write(ref _levels[s], level);
                }
            }

            for (var t = 0; t < _tracks.Count; t++)
            {
                var span = accumulator.AsSpan(0, frames * AudioFormat.Channels);
                span.Clear();
                foreach (var s in _tracks[t].SourceIndexes)
                {
                    AudioMath.MixInto(span, sourceBuffers[s].AsSpan(0, bytes), gains[s]);
                }

                limiters[t].Process(span);
                AudioMath.ToPcm16(span, output.AsSpan(0, bytes));
                _sinks[t].Write(output.AsSpan(0, bytes));
            }

            framesWritten += frames;
        }
    }

    /// <summary>Highest short-term RMS level (after volume and gain, 1 = full scale) of a source since the previous call.</summary>
    public float TakeLevel(int sourceIndex) => Interlocked.Exchange(ref _levels[sourceIndex], 0f);

    private void UpdateGains(float[] gains)
    {
        var sources = _config().Audio.Sources;
        for (var i = 0; i < _sourceIds.Count; i++)
        {
            var cfg = sources.FirstOrDefault(s => s.Id.Equals(_sourceIds[i], StringComparison.OrdinalIgnoreCase));
            gains[i] = cfg is null ? 0f : AudioMath.GainFromVolume(cfg.Volume, cfg.Enabled) * AudioMath.GainFromDecibels(cfg.GainDb);
        }
    }

    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive)
        {
            _thread.Join(1000);
        }
    }
}
