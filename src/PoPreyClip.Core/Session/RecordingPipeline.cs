using PoPreyClip.Audio;
using PoPreyClip.Config;
using PoPreyClip.Games;
using PoPreyClip.Native;
using PoPreyClip.Recording;

namespace PoPreyClip.Session;

/// <summary>Everything that runs while recording: audio capture, mixer, ffmpeg buffer and exporter.</summary>
internal sealed class RecordingPipeline : IDisposable
{
    private readonly List<AudioSourceRunner> _runners;
    private readonly List<PipeAudioSink> _sinks;

    private RecordingPipeline(
        List<AudioSourceRunner> runners,
        List<PipeAudioSink> sinks,
        List<AudioTrackSpec> tracks,
        ReplayRecorder recorder,
        AudioPacer pacer,
        ClipExporter exporter)
    {
        _runners = runners;
        _sinks = sinks;
        Tracks = tracks;
        Recorder = recorder;
        Pacer = pacer;
        Exporter = exporter;
        SourceIds = runners.Select(r => r.Id).ToList();
    }

    public IReadOnlyList<AudioTrackSpec> Tracks { get; }

    public IReadOnlyList<string> SourceIds { get; }

    public ReplayRecorder Recorder { get; }

    public AudioPacer Pacer { get; }

    public ClipExporter Exporter { get; }

    public static RecordingPipeline Start(AppConfig config, Func<AppConfig> live, GameDetector detector)
    {
        var enabledSources = config.Audio.Sources.Where(s => s.Enabled).ToList();
        var runners = enabledSources.Select(s => new AudioSourceRunner(s, detector)).ToList();
        var tracks = AudioTrackPlan.Build(enabledSources, config.Audio);
        var sinks = new List<PipeAudioSink>();
        ReplayRecorder? recorder = null;
        try
        {
            foreach (var (track, index) in tracks.Select((t, i) => (t, i)))
            {
                sinks.Add(new PipeAudioSink($"popreyclip-{Environment.ProcessId}-{Guid.NewGuid():N}-{index}", track.Title));
            }

            var processes = Win32.GetProcesses();
            foreach (var runner in runners)
            {
                runner.Tick(processes);
            }

            recorder = new ReplayRecorder(config, live);
            recorder.Start(sinks);
            var pacer = new AudioPacer(
                runners.Select(r => r.Fifo).ToList(),
                runners.Select(r => r.Id).ToList(),
                tracks,
                sinks.Cast<IAudioSink>().ToList(),
                live);
            pacer.Start();
            var exporter = new ClipExporter(live, recorder, tracks.Select(t => t.Title).ToList());
            return new RecordingPipeline(runners, sinks, tracks, recorder, pacer, exporter);
        }
        catch
        {
            recorder?.Dispose();
            sinks.ForEach(s => s.Dispose());
            runners.ForEach(r => r.Dispose());
            throw;
        }
    }

    /// <summary>Called every two seconds: follows apps that start or close, and trims the buffer.</summary>
    public void Tick()
    {
        var processes = Win32.GetProcesses();
        foreach (var runner in _runners)
        {
            runner.Tick(processes);
        }

        Recorder.Cleanup();
    }

    public void Dispose()
    {
        Pacer.Dispose();
        _sinks.ForEach(s => s.Dispose());
        _runners.ForEach(r => r.Dispose());
        Recorder.Dispose();
    }
}
