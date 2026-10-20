using System.Text.Json;
using PoPreyClip.Audio;
using PoPreyClip.Config;
using PoPreyClip.Games;
using PoPreyClip.Native;
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

    /// <summary>Game whose window is being filmed (0 when filming the monitor).</summary>
    private int _sourceGamePid;

    private long _pipelineStartedTick;

    private int _restarting;

    private bool _levelPreview;

    private AudioLevelMonitor? _levelMonitor;

    /// <summary>Games whose window could not be captured; they are filmed through the monitor instead.</summary>
    private readonly HashSet<int> _windowCaptureFailed = [];

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
        PoPreyClip.Updates.FfmpegManager.Installed += OnFfmpegInstalled;
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

    /// <summary>
    /// Raised when a game is detected and the replay buffer is on for it: it just turned on by itself, or it was
    /// already on. Used for the "Pronto para clipar" notice.
    /// </summary>
    public event Action<DetectedGame>? GameReady;

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
        if (_disposed)
        {
            return;
        }

        if (game is not null && IsRecording)
        {
            GameReady?.Invoke(game);
        }

        if (!_store.Current.AutoRecordGames)
        {
            return;
        }

        if (game is not null && !IsRecording)
        {
            Log.Info($"{game.DisplayName} aberto: ligando a gravação.");
            _ = Task.Run(() => StartAutomatically(game));
        }
        else if (game is null && IsRecording && _autoStarted)
        {
            Log.Info("Jogo fechado: desligando a gravação para economizar recursos.");
            _ = Task.Run(StopCore);
        }
    }

    /// <summary>A game opened while FFmpeg was still downloading: start now that it is ready.</summary>
    private void OnFfmpegInstalled(Version version)
    {
        if (!IsRecording && _detector.CurrentGame is { } game)
        {
            OnGameChanged(game);
        }
    }

    private void StartAutomatically(DetectedGame game)
    {
        try
        {
            StartCore(automatic: true);
            GameReady?.Invoke(game);
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
                throw new InvalidOperationException(PoPreyClip.Updates.FfmpegManager.Progress is { } progress
                    ? $"O FFmpeg ainda está sendo baixado ({progress:P0}). Com um jogo aberto, a gravação começa sozinha quando terminar."
                    : "O FFmpeg não foi encontrado. O PoPreyClip baixa ele sozinho: confira a internet e clique em Procurar agora, em Configurações → Atualizações.");
            }

            _detector.Poll();
            var source = ChooseVideoSource(config);
            // Both would capture the same sources; the recording reports the levels from now on.
            StopLevelMonitor();
            _pipeline = RecordingPipeline.Start(config, () => _store.Current, _detector, source);
            _signature = Signature(config);
            _autoStarted = automatic;
            _sourceGamePid = source.IsWindow ? _detector.CurrentGame?.ProcessId ?? 0 : 0;
            _pipelineStartedTick = Environment.TickCount64;
            Log.Info($"Gravação iniciada. Vídeo: {source.Description}. Faixas: {string.Join(" | ", _pipeline.Tracks.Select((t, i) => $"{i}: {t.Title}"))}");
        }

        RecordingChanged?.Invoke(true);
    }

    /// <summary>The game's window when there is a game and window capture is on and works; otherwise the monitor.</summary>
    private VideoSource ChooseVideoSource(AppConfig config)
    {
        var monitor = VideoSource.Monitor(config.Video.MonitorIndex);
        var game = _detector.CurrentGame;
        if (game is null || !CanFilmWindow(config, game))
        {
            return monitor;
        }

        var window = FindGameWindow(game);
        return window == IntPtr.Zero ? monitor : VideoSource.ForWindow(window, game.DisplayName);
    }

    private bool CanFilmWindow(AppConfig config, DetectedGame game) =>
        config.Video.Capture == VideoConfig.CaptureGameWindow
        && !_windowCaptureFailed.Contains(game.ProcessId)
        && WindowCaptureSupport.IsAvailable(config.FfmpegExe);

    private static IntPtr FindGameWindow(DetectedGame game) =>
        Win32.FindMainWindow(Win32.ProcessTree(Win32.GetProcesses(), game.ProcessId));

    /// <summary>
    /// Watchdog for the video source, run every two seconds. Returns why the recording must restart with another
    /// source, or null when the current one is fine: the game's window was replaced (splash screen → game),
    /// the game closed or changed, a game opened while filming the monitor, or the window stopped sending frames.
    /// </summary>
    private string? CheckVideoSource(RecordingPipeline pipeline)
    {
        if (Environment.TickCount64 - _pipelineStartedTick < 5000)
        {
            return null;
        }

        var config = _store.Current;
        var game = _detector.CurrentGame;
        var source = pipeline.Recorder.Source;
        if (!source.IsWindow)
        {
            return game is not null && CanFilmWindow(config, game) && FindGameWindow(game) != IntPtr.Zero
                ? $"{game.DisplayName} aberto: passando a gravar só a janela do jogo."
                : null;
        }

        if (game is null)
        {
            // In idle mode the recording is about to stop anyway.
            return _autoStarted ? null : "O jogo fechou: voltando a gravar o monitor.";
        }

        if (game.ProcessId != _sourceGamePid)
        {
            return $"{game.DisplayName} aberto: trocando a captura para a janela dele.";
        }

        if (!Win32.IsWindow(source.Window))
        {
            return $"A janela de {game.DisplayName} mudou: reconectando a captura.";
        }

        // The player is looking at the game but no image arrives: this game can't be filmed through its window.
        if (Win32.GetForegroundWindow() == source.Window && !Win32.IsIconic(source.Window) && pipeline.Recorder.SecondsSinceLastFrame > 12)
        {
            _windowCaptureFailed.Add(game.ProcessId);
            return $"A janela de {game.DisplayName} não está mandando imagem. Gravando o monitor no lugar.";
        }

        return null;
    }

    /// <summary>Stops and starts the recording off the calling thread, keeping whether it was started automatically.</summary>
    private void Restart(string reason)
    {
        if (Interlocked.Exchange(ref _restarting, 1) == 1)
        {
            return;
        }

        Log.Info(reason);
        _ = Task.Run(() =>
        {
            try
            {
                var wasAutomatic = _autoStarted;
                StopCore();
                if (!_disposed)
                {
                    StartCore(wasAutomatic);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or ObjectDisposedException)
            {
                Log.Error(ex.Message);
                Failed?.Invoke(ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _restarting, 0);
            }
        });
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
        if (Volatile.Read(ref _restarting) == 0)
        {
            lock (_gate)
            {
                UpdateLevelMonitor();
            }
        }

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

    /// <summary>
    /// Shows audio levels while not recording (the Audio page asks for it while it is open). The sources are captured
    /// only for the meters, and the capture stops as soon as a recording starts or the preview is turned off.
    /// </summary>
    public bool LevelPreview
    {
        get => _levelPreview;
        set
        {
            lock (_gate)
            {
                _levelPreview = value;
                UpdateLevelMonitor();
            }
        }
    }

    /// <summary>Highest level (0 to 2) of an audio source since the previous call; 0 when nothing is captured.</summary>
    public float TakeLevel(string sourceId)
    {
        var pipeline = _pipeline;
        if (pipeline is null)
        {
            return _levelMonitor?.TakeLevel(sourceId) ?? 0;
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

        Restart("A configuração de gravação mudou. Reiniciando a gravação para aplicar...");
    }

    private void MonitorTick()
    {
        if (!Monitor.TryEnter(_gate))
        {
            return;
        }

        string? failure = null;
        string? restart = null;
        try
        {
            if (_disposed || Volatile.Read(ref _restarting) == 1)
            {
                return;
            }

            _detector.Poll();
            if (_pipeline is { } pipeline)
            {
                var source = pipeline.Recorder.Source;
                if (pipeline.Recorder.HasExited && source.IsWindow && !Win32.IsWindow(source.Window))
                {
                    // The filmed window closed, so the video process ended normally. Film the game's new window, or the
                    // monitor if the game closed (in idle mode the recording is about to stop anyway).
                    var game = _detector.CurrentGame;
                    restart = game is not null ? $"A janela de {game.DisplayName} mudou: reconectando a captura."
                        : _autoStarted ? null
                        : "O jogo fechou: voltando a gravar o monitor.";
                }
                else if (pipeline.Recorder.HasExited && source.IsWindow && _detector.CurrentGame is { } game)
                {
                    // The window is still there but could not be filmed: use the monitor for this game from now on.
                    _windowCaptureFailed.Add(game.ProcessId);
                    Log.Warn($"A captura da janela falhou: {pipeline.Recorder.ReadFfmpegLogTail()}");
                    restart = $"A captura da janela de {game.DisplayName} parou. Gravando o monitor no lugar.";
                }
                else if (pipeline.Recorder.HasExited)
                {
                    failure = $"O FFmpeg parou (código {pipeline.Recorder.ExitCode}). {pipeline.Recorder.ReadFfmpegLogTail()}";
                }
                else
                {
                    restart = CheckVideoSource(pipeline);
                    if (restart is null)
                    {
                        pipeline.Tick();
                    }
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

        if (restart is not null)
        {
            Restart(restart);
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
        PoPreyClip.Updates.FfmpegManager.Installed -= OnFfmpegInstalled;
        _monitor.Dispose();
        Stop();
        lock (_gate)
        {
            StopLevelMonitor();
        }
    }

    /// <summary>Starts or stops the level preview to match the page and the recording. Called with the gate held.</summary>
    private void UpdateLevelMonitor()
    {
        if (_levelPreview && _pipeline is null && !_disposed)
        {
            _levelMonitor ??= new AudioLevelMonitor(() => _store.Current, _detector);
        }
        else
        {
            StopLevelMonitor();
        }
    }

    private void StopLevelMonitor()
    {
        _levelMonitor?.Dispose();
        _levelMonitor = null;
    }
}
