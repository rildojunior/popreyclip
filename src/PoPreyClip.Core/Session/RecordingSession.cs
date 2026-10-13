using System.Text.Json;
using PoPreyClip.Audio;
using PoPreyClip.Config;
using PoPreyClip.Games;
using PoPreyClip.Recording;

namespace PoPreyClip.Session;

public sealed record ClipSavedInfo(string Path, string Game);

/// <summary>
/// Public entry point used by the desktop app and the command-line tool. Starts and stops recording,
/// saves clips, reports audio levels, and restarts recording by itself when a setting that defines the
/// recording (video, track layout, which sources exist) changes. Volume changes apply without restarting.
/// </summary>
public sealed class RecordingSession : IDisposable
{
    private readonly ConfigStore _store;
    private readonly GameDetector _detector;
    private readonly object _gate = new();
    private readonly Timer _monitor;
    private readonly ScreenshotCapture _screenshots = new();
    private RecordingPipeline? _pipeline;
    private string? _signature;
    private bool _disposed;

    /// <summary>True when the current recording was started because a game opened (not by the user).</summary>
    private volatile bool _autoStarted;

    /// <summary>Last seen value of the idle-mode setting, to react only when the user turns it on.</summary>
    private bool _autoRecordWasOn;

    public RecordingSession(ConfigStore store)
    {
        _store = store;
        _autoRecordWasOn = store.Current.AutoRecordGames;
        _detector = new GameDetector(() => store.Current);
        _detector.GameChanged += game =>
        {
            GameChanged?.Invoke(game);
            OnGameChanged(game);
        };
        _store.Changed += OnConfigChanged;
        ReplayRecorder.CleanupStaleBuffers();
        _monitor = new Timer(_ => MonitorTick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    public bool IsRecording => _pipeline is not null;

    public DetectedGame? CurrentGame => _detector.CurrentGame;

    public string ClipGameName => _detector.ClipGameName;

    public IReadOnlyList<AudioTrackSpec> Tracks => _pipeline?.Tracks ?? [];

    public event Action<bool>? RecordingChanged;

    public event Action<DetectedGame?>? GameChanged;

    public event Action<ClipSavedInfo>? ClipSaved;

    /// <summary>Raised after a screenshot is saved. The path and game reuse <see cref="ClipSavedInfo"/>.</summary>
    public event Action<ClipSavedInfo>? ScreenshotSaved;

    /// <summary>Raised when recording stops because of an error.</summary>
    public event Action<string>? Failed;

    /// <summary>
    /// Starts recording by the user's choice: it stays on until the user stops it, even after the game closes.
    /// Throws <see cref="InvalidOperationException"/> with a readable message on failure.
    /// </summary>
    public void Start() => StartCore(automatic: false);

    /// <summary>Stops recording by the user's choice. A game that is already open will not turn it back on.</summary>
    public void Stop()
    {
        _autoStarted = false;
        StopCore();
    }

    /// <summary>
    /// Idle mode ("gravar só com jogo aberto"): start when a game appears, stop when the game that started it closes.
    /// Runs off the monitor thread so detection never waits for ffmpeg to start or stop.
    /// </summary>
    private void OnGameChanged(DetectedGame? game)
    {
        if (!_store.Current.AutoRecordGames || _disposed)
        {
            return;
        }

        if (game is not null && !IsRecording)
        {
            Log.Info($"{game.DisplayName} aberto: ligando a gravação.");
            _ = Task.Run(() => StartAutomatically());
        }
        else if (game is null && IsRecording && _autoStarted)
        {
            Log.Info("Jogo fechado: desligando a gravação para economizar recursos.");
            _ = Task.Run(StopCore);
        }
    }

    private void StartAutomatically()
    {
        try
        {
            StartCore(automatic: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            Log.Error(ex.Message);
            Failed?.Invoke(ex.Message);
        }
    }

    private void StartCore(bool automatic)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline is not null)
            {
                return;
            }

            var config = _store.Current;
            if (!Ffmpeg.IsAvailable(config.FfmpegExe) || !Ffmpeg.IsAvailable(config.FfprobeExe))
            {
                throw new InvalidOperationException("FFmpeg não foi encontrado. Instale com: winget install Gyan.FFmpeg");
            }

            _detector.Poll();
            _pipeline = RecordingPipeline.Start(config, () => _store.Current, _detector);
            _signature = Signature(config);
            _autoStarted = automatic;
            Log.Info($"Gravação iniciada. Faixas: {string.Join(" | ", _pipeline.Tracks.Select((t, i) => $"{i}: {t.Title}"))}");
        }

        RecordingChanged?.Invoke(true);
    }

    private void StopCore()
    {
        RecordingPipeline? pipeline;
        lock (_gate)
        {
            pipeline = _pipeline;
            _pipeline = null;
        }

        if (pipeline is null)
        {
            return;
        }

        pipeline.Dispose();
        Log.Info("Gravação parada.");
        RecordingChanged?.Invoke(false);
    }

    /// <summary>Saves a PNG of the recorded monitor into the current game's Screenshots folder. Works while stopped too.</summary>
    public async Task<string?> SaveScreenshotAsync()
    {
        var game = _detector.ClipGameName;
        try
        {
            var path = await _screenshots.CaptureAsync(_store.Current, game);
            if (path is not null)
            {
                ScreenshotSaved?.Invoke(new ClipSavedInfo(path, game));
            }

            return path;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Error($"Não foi possível salvar a captura de tela: {ex.Message}");
            return null;
        }
    }

    public async Task<string?> SaveClipAsync()
    {
        var pipeline = _pipeline;
        if (pipeline is null)
        {
            Log.Warn("A gravação está parada. Inicie a gravação para salvar clipes.");
            return null;
        }

        var game = _detector.ClipGameName;
        Log.Info($"Salvando clipe de {_store.Current.ClipSeconds}s ({game})...");
        try
        {
            var path = await pipeline.Exporter.SaveAsync(game);
            if (path is not null)
            {
                Log.Info($"Clipe salvo: {path}");
                ClipSaved?.Invoke(new ClipSavedInfo(path, game));
            }

            return path;
        }
        catch (InvalidOperationException ex)
        {
            Log.Error(ex.Message);
            return null;
        }
    }

    /// <summary>Highest level (0 to 2) of an audio source since the previous call; 0 when not recording.</summary>
    public float TakeLevel(string sourceId)
    {
        var pipeline = _pipeline;
        if (pipeline is null)
        {
            return 0;
        }

        for (var i = 0; i < pipeline.SourceIds.Count; i++)
        {
            if (pipeline.SourceIds[i].Equals(sourceId, StringComparison.OrdinalIgnoreCase))
            {
                return pipeline.Pacer.TakeLevel(i);
            }
        }

        return 0;
    }

    private void OnConfigChanged(AppConfig config)
    {
        var autoTurnedOn = config.AutoRecordGames && !_autoRecordWasOn;
        _autoRecordWasOn = config.AutoRecordGames;
        if (_pipeline is null)
        {
            // Idle mode just turned on while a game is already open: start right away.
            if (autoTurnedOn && _detector.CurrentGame is { } game && !_disposed)
            {
                OnGameChanged(game);
            }

            return;
        }

        if (Signature(config) == _signature)
        {
            return;
        }

        Log.Info("A configuração de gravação mudou. Reiniciando a gravação para aplicar...");
        _ = Task.Run(() =>
        {
            try
            {
                var wasAutomatic = _autoStarted;
                StopCore();
                StartCore(wasAutomatic);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Runtime.InteropServices.COMException)
            {
                Log.Error(ex.Message);
                Failed?.Invoke(ex.Message);
            }
        });
    }

    private void MonitorTick()
    {
        if (!Monitor.TryEnter(_gate))
        {
            return;
        }

        string? failure = null;
        try
        {
            if (_disposed)
            {
                return;
            }

            _detector.Poll();
            if (_pipeline is { } pipeline)
            {
                if (pipeline.Recorder.HasExited)
                {
                    failure = $"O FFmpeg parou (código {pipeline.Recorder.ExitCode}). {pipeline.Recorder.ReadFfmpegLogTail()}";
                }
                else
                {
                    pipeline.Tick();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn(ex.Message);
        }
        finally
        {
            Monitor.Exit(_gate);
        }

        if (failure is not null)
        {
            Log.Error(failure);
            Stop();
            Failed?.Invoke(failure);
        }
    }

    /// <summary>Settings that require a new ffmpeg process when they change.</summary>
    internal static string Signature(AppConfig config) => JsonSerializer.Serialize(new
    {
        config.Video,
        config.Audio.MultipleAudioTracks,
        config.Audio.IncludeMixTrack,
        config.Audio.AudioBitrateKbps,
        Sources = config.Audio.Sources.Where(s => s.Enabled).Select(s => new { s.Id, s.Name, s.Device }),
    });

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _store.Changed -= OnConfigChanged;
        _monitor.Dispose();
        Stop();
    }
}
