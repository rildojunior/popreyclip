using PoPreyClip.Config;
using PoPreyClip.Games;
using PoPreyClip.Native;

namespace PoPreyClip.Audio;

/// <summary>
/// Captures the enabled audio sources only to show their levels, so volumes can be set before recording. Runs while
/// the Audio page is open with the preview turned on and nothing is being recorded; the recording reports its own
/// levels. Nothing is written anywhere: each read empties the source buffer.
/// </summary>
internal sealed class AudioLevelMonitor : IDisposable
{
    /// <summary>Same 10 ms blocks the recorder measures, so both show the same level.</summary>
    private const int BlockBytes = AudioFormat.SampleRate / 100 * AudioFormat.BytesPerFrame;

    private readonly Func<AppConfig> _config;
    private readonly GameDetector _games;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private volatile State _state = new([], [], "");

    public AudioLevelMonitor(Func<AppConfig> config, GameDetector games)
    {
        _config = config;
        _games = games;
        _thread = new Thread(Run) { IsBackground = true, Name = "PoPreyClip audio levels", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Highest level (after volume and gain) of a source since the previous call; 0 when it is not captured.</summary>
    public float TakeLevel(string sourceId)
    {
        var state = _state;
        for (var i = 0; i < state.Runners.Count; i++)
        {
            if (state.Runners[i].Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
            {
                return Interlocked.Exchange(ref state.Levels[i], 0f);
            }
        }

        return 0;
    }

    private void Run()
    {
        var buffer = new byte[BlockBytes];
        var nextTick = 0L;
        while (_running)
        {
            var config = _config();
            if (Environment.TickCount64 >= nextTick)
            {
                // Sources added, removed or switched to another microphone are picked up here, like the recorder does.
                Rebuild(config);
                var processes = Win32.GetProcesses();
                foreach (var runner in _state.Runners)
                {
                    runner.Tick(processes);
                }

                nextTick = Environment.TickCount64 + 1000;
            }

            var state = _state;
            for (var i = 0; i < state.Runners.Count; i++)
            {
                var source = config.Audio.Sources.FirstOrDefault(s => s.Id.Equals(state.Runners[i].Id, StringComparison.OrdinalIgnoreCase));
                var gain = source is null ? 0f : AudioMath.GainFromVolume(source.Volume, source.Enabled) * AudioMath.GainFromDecibels(source.GainDb);
                var fifo = state.Runners[i].Fifo;
                while (fifo.Available >= BlockBytes)
                {
                    fifo.Read(buffer);
                    var level = AudioMath.Rms(buffer) * gain;
                    if (level > Volatile.Read(ref state.Levels[i]))
                    {
                        Volatile.Write(ref state.Levels[i], level);
                    }
                }
            }

            Thread.Sleep(30);
        }

        foreach (var runner in _state.Runners)
        {
            runner.Dispose();
        }
    }

    private void Rebuild(AppConfig config)
    {
        var sources = config.Audio.Sources.Where(s => s.Enabled).ToList();
        var signature = string.Join("|", sources.Select(s => $"{s.Id}:{s.Device}"));
        if (signature == _state.Signature)
        {
            return;
        }

        var old = _state;
        var runners = sources.Select(s => new AudioSourceRunner(s, _games, quiet: true)).ToList();
        _state = new State(runners, new float[runners.Count], signature);
        foreach (var runner in old.Runners)
        {
            runner.Dispose();
        }
    }

    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(2000);
        }
    }

    private sealed record State(IReadOnlyList<AudioSourceRunner> Runners, float[] Levels, string Signature);
}
