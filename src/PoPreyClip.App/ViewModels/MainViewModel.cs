using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Globalization;
using PoPreyClip.App.Controls;
using PoPreyClip.Clips;
using PoPreyClip.Config;
using PoPreyClip.Games;
using PoPreyClip.Hotkeys;
using PoPreyClip.Library;
using PoPreyClip.Session;
using PoPreyClip.Voice;

namespace PoPreyClip.App.ViewModels;

public enum AppPage
{
    Home,
    Audio,
    Clips,
    Settings,
}

public sealed record LogLine(string Time, string Message, LogLevel Level)
{
    public bool IsError => Level == LogLevel.Error;

    public bool IsWarning => Level == LogLevel.Warning;
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 150;
    private readonly ConfigStore _store;
    private readonly RecordingSession _session;
    private readonly HotkeyManager _hotkeys;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _toastTimer;
    private readonly ClipSoundPlayer _sounds = new();
    private readonly VoiceClipTrigger _voice;
    private AppPage _page = AppPage.Home;
    private bool _isRecording;
    private bool _isBusy;
    private string _currentGame = "Nenhum jogo detectado";
    private string? _errorMessage;
    private string? _toastMessage;
    private string? _toastPath;
    private string _tracksText = "";

    public MainViewModel(ConfigStore store, RecordingSession session, HotkeyManager hotkeys, Action exitForUpdate)
    {
        _store = store;
        _session = session;
        _hotkeys = hotkeys;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Audio = new AudioViewModel(store, session);
        Clips = new ClipsViewModel(store);
        Clips.EditSaved += (_, overwrote) =>
        {
            ShowNotice(overwrote ? "Clipe editado e substituído" : "Clipe editado salvo como novo vídeo");
            Clips.ScheduleRefresh();
        };
        Clips.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClipsViewModel.Summary))
            {
                UpdateGameStats();
            }
        };
        Updates = new UpdatesViewModel(store, exitForUpdate);
        _voice = new VoiceClipTrigger(store, session);
        Settings = new SettingsViewModel(store, SetHotkey, _sounds)
        {
            Updates = Updates,
            Voice = new VoiceCommandsViewModel(store, _voice, _dispatcher),
        };
        Settings.SectionNavigationRequested += () => Page = AppPage.Settings;

        ToggleRecordingCommand = new AsyncRelayCommand(ToggleRecordingAsync, () => !IsBusy);
        SaveClipCommand = new AsyncRelayCommand(SaveClipAsync, () => IsRecording);
        SaveScreenshotCommand = new AsyncRelayCommand(SaveScreenshotAsync);
        NavigateCommand = new RelayCommand(p =>
        {
            if (p is AppPage page)
            {
                Page = page;
            }
            else if (p is string text && Enum.TryParse<AppPage>(text, out var parsed))
            {
                Page = parsed;
            }
        });
        DismissErrorCommand = new RelayCommand(() => ErrorMessage = null);
        ToastOpenCommand = new RelayCommand(() =>
        {
            if (_toastPath is not null)
            {
                ClipLibrary.Play(_toastPath);
            }
        });
        ToastRevealCommand = new RelayCommand(() =>
        {
            if (_toastPath is not null)
            {
                ClipLibrary.RevealInExplorer(_toastPath);
            }
        });
        CloseToastCommand = new RelayCommand(() => ToastMessage = null);
        GameActionCommand = new RelayCommand(() =>
        {
            if (_session.CurrentGame is { } game)
            {
                Clips.OpenGameFolder(ClipPathBuilder.SanitizeFolderName(game.DisplayName));
            }
            else
            {
                Settings.Sections.First(s => s.Id == "jogos").GoCommand.Execute(null);
            }
        });
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(7), DispatcherPriority.Normal, (_, _) =>
        {
            _toastTimer!.Stop();
            ToastMessage = null;
        }, _dispatcher);
        _toastTimer.Stop();

        Log.Logged += OnLogged;
        _session.RecordingChanged += recording => _dispatcher.InvokeAsync(() =>
        {
            IsRecording = recording;
            UpdateTracks();
        });
        _session.GameChanged += game => _dispatcher.InvokeAsync(() => UpdateGame(game));
        _session.GameReady += game => _dispatcher.InvokeAsync(() => GameReady?.Invoke(game));
        _session.ClipSaved += info => _dispatcher.InvokeAsync(() => OnClipSaved(info));
        _session.ScreenshotSaved += info => _dispatcher.InvokeAsync(() => OnScreenshotSaved(info));
        _session.Failed += message => _dispatcher.InvokeAsync(() => ErrorMessage = message);
        _store.Changed += _ => _dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(HotkeyHint));
            OnPropertyChanged(nameof(StatusDetail));
            OnPropertyChanged(nameof(StatusTitle));
            OnPropertyChanged(nameof(IsWaitingForGame));
            OnPropertyChanged(nameof(ToggleToolTip));
            OnPropertyChanged(nameof(TrayText));
        });

        HotkeyBox.CapturingChanged += capturing =>
        {
            if (capturing)
            {
                _hotkeys.Suspend();
            }
            else
            {
                _hotkeys.Resume();
            }
        };
        _hotkeys.RegistrationFailed += (name, message) => _dispatcher.InvokeAsync(() =>
        {
            switch (name)
            {
                case SettingsViewModel.SaveClipHotkeyName:
                    Settings.SaveHotkeyError = message;
                    break;
                case SettingsViewModel.ScreenshotHotkeyName:
                    Settings.ScreenshotHotkeyError = message;
                    break;
                default:
                    Settings.ToggleHotkeyError = message;
                    break;
            }
        });

        Settings.SaveHotkeyError = SetHotkey(SettingsViewModel.SaveClipHotkeyName, store.Current.Hotkey);
        Settings.ToggleHotkeyError = SetHotkey(SettingsViewModel.ToggleRecordingHotkeyName, store.Current.ToggleRecordingHotkey);
        Settings.ScreenshotHotkeyError = SetHotkey(SettingsViewModel.ScreenshotHotkeyName, store.Current.ScreenshotHotkey);
        UpdateGame(session.CurrentGame);
    }

    public AudioViewModel Audio { get; }

    public ClipsViewModel Clips { get; }

    public SettingsViewModel Settings { get; }

    public UpdatesViewModel Updates { get; }

    /// <summary>Shows a short notice in the corner (no clip actions).</summary>
    public void ShowNotice(string message)
    {
        _toastPath = null;
        ToastMessage = message;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public ObservableCollection<LogLine> LogLines { get; } = [];

    public ICommand ToggleRecordingCommand { get; }

    public ICommand SaveClipCommand { get; }

    public ICommand SaveScreenshotCommand { get; }

    public ICommand NavigateCommand { get; }

    public ICommand DismissErrorCommand { get; }

    public ICommand ToastOpenCommand { get; }

    public ICommand ToastRevealCommand { get; }

    public ICommand CloseToastCommand { get; }

    /// <summary>Raised when a clip is saved, so the window can show a tray notification while hidden.</summary>
    public event Action<ClipSavedInfo>? ClipSaved;

    /// <summary>Raised when a screenshot is saved, for the tray notification.</summary>
    public event Action<ClipSavedInfo>? ScreenshotSaved;

    /// <summary>Raised when a game is detected with clip capture on, for the "Pronto para clipar" notice.</summary>
    public event Action<DetectedGame>? GameReady;

    public AppPage Page
    {
        get => _page;
        set
        {
            if (SetProperty(ref _page, value))
            {
                if (value == AppPage.Home)
                {
                    // "há 5 min" moves on while the home page is not visible.
                    UpdateGameStats();
                }

                OnPropertyChanged(nameof(IsHomePage));
                OnPropertyChanged(nameof(IsAudioPage));
                OnPropertyChanged(nameof(IsClipsPage));
                OnPropertyChanged(nameof(IsSettingsPage));
            }
        }
    }

    public bool IsHomePage
    {
        get => Page == AppPage.Home;
        set
        {
            if (value)
            {
                Page = AppPage.Home;
            }
        }
    }

    public bool IsAudioPage
    {
        get => Page == AppPage.Audio;
        set
        {
            if (value)
            {
                Page = AppPage.Audio;
            }
        }
    }

    public bool IsClipsPage
    {
        get => Page == AppPage.Clips;
        set
        {
            if (value)
            {
                Page = AppPage.Clips;
            }
        }
    }

    public bool IsSettingsPage
    {
        get => Page == AppPage.Settings;
        set
        {
            if (value)
            {
                Page = AppPage.Settings;
            }
        }
    }

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetProperty(ref _isRecording, value))
            {
                OnPropertyChanged(nameof(StatusTitle));
                OnPropertyChanged(nameof(IsWaitingForGame));
                OnPropertyChanged(nameof(StatusDetail));
                OnPropertyChanged(nameof(ToggleText));
                OnPropertyChanged(nameof(ToggleToolTip));
                OnPropertyChanged(nameof(TrayText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(StatusTitle));
                OnPropertyChanged(nameof(ToggleText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string StatusTitle => IsBusy ? (IsRecording ? "Desativando..." : "Ativando...") : IsRecording ? "Captura de clipes ativa" : IsWaitingForGame ? "Em espera" : "Desativada";

    /// <summary>Idle mode with no recording: waiting for a game to open.</summary>
    public bool IsWaitingForGame => !IsRecording && _store.Current.AutoRecordGames;

    public string StatusDetail => IsRecording
        ? $"Os últimos {Settings.ClipSecondsText} ficam guardados. Pressione {_store.Current.Hotkey} para salvar."
        : IsWaitingForGame
            ? "Esperando um jogo abrir. A captura de clipes liga sozinha quando ele abrir e desliga quando ele fechar."
            : "Ative a captura de clipes para poder salvar clipes.";

    public string ToggleText => IsRecording ? "Desativar captura de clipes" : "Ativar captura de clipes";

    public string ToggleToolTip => IsRecording
        ? "Desliga a captura de clipes. Se um jogo estiver aberto, ela só liga sozinha de novo quando outro jogo abrir."
        : IsWaitingForGame
            ? $"A captura de clipes liga sozinha quando o jogo é detectado. Ative à mão só se o seu jogo abriu e não foi detectado. Os últimos {Settings.ClipSecondsText} ficam guardados, e nada vira arquivo até você apertar {_store.Current.Hotkey}."
            : $"Os últimos {Settings.ClipSecondsText} ficam guardados, e nada vira arquivo até você apertar {_store.Current.Hotkey}.";

    /// <summary>App status for the notification-area icon: "Em espera", "Captura de clipes ativa (Jogo)"...</summary>
    public string TrayStatus
    {
        get
        {
            if (!IsRecording)
            {
                return IsWaitingForGame ? "Em espera" : "Captura de clipes desativada";
            }

            return _session.CurrentGame is { } game ? $"Captura de clipes ativa ({game.DisplayName})" : "Captura de clipes ativa";
        }
    }

    /// <summary>Text of the notification-area icon when the mouse is over it (Windows allows up to 127 characters).</summary>
    public string TrayText
    {
        get
        {
            const int MaxLength = 127;
            var text = $"PoPreyClip · {TrayStatus}";
            return text.Length <= MaxLength ? text : text[..(MaxLength - 2)] + "…)";
        }
    }

    public string HotkeyHint
    {
        get
        {
            var c = _store.Current;
            var parts = new List<string> { $"{c.Hotkey} salva um clipe" };
            if (!string.IsNullOrWhiteSpace(c.ScreenshotHotkey))
            {
                parts.Add($"{c.ScreenshotHotkey} captura a tela");
            }

            if (!string.IsNullOrWhiteSpace(c.ToggleRecordingHotkey))
            {
                parts.Add($"{c.ToggleRecordingHotkey} ativa ou desativa a captura de clipes");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>"Jogo atual" card: open the game's clip folder, or (no game) go to Configurações → Jogos.</summary>
    public ICommand GameActionCommand { get; }

    public string GameActionText => IsGameOpen ? "Abrir pasta do jogo" : "Meu jogo não foi detectado";

    /// <summary>First number of the card: clips of the open game, or of every game.</summary>
    public string GameStatValue { get; private set; } = "0";

    public string GameStatLabel { get; private set; } = "";

    /// <summary>Second number: when the last clip of the game was saved, or the space all clips use.</summary>
    public string GameStat2Value { get; private set; } = "";

    public string GameStat2Label { get; private set; } = "";

    /// <summary>A game (or watched app) is open right now.</summary>
    public bool IsGameOpen => _session.CurrentGame is not null;

    public string CurrentGame
    {
        get => _currentGame;
        private set => SetProperty(ref _currentGame, value);
    }

    public string TracksText
    {
        get => _tracksText;
        private set => SetProperty(ref _tracksText, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string? ToastMessage
    {
        get => _toastMessage;
        private set
        {
            if (SetProperty(ref _toastMessage, value))
            {
                OnPropertyChanged(nameof(IsToastVisible));
                OnPropertyChanged(nameof(HasToastActions));
            }
        }
    }

    public bool IsToastVisible => ToastMessage is not null;

    public bool HasToastActions => _toastPath is not null;

    public string ConfigFolderPath => _store.Current.ClipFolder;

    public void StartOnLaunch()
    {
        if (_store.Current.StartRecordingOnLaunch && !_store.Current.AutoRecordGames)
        {
            ToggleRecordingCommand.Execute(null);
        }
    }

    public async Task ToggleRecordingAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var wasRecording = _session.IsRecording;
            await Task.Run(() =>
            {
                if (wasRecording)
                {
                    _session.Stop();
                }
                else
                {
                    _session.Start();
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            ErrorMessage = ex.Message;
            Log.Error(ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsRecording = _session.IsRecording;
            UpdateTracks();
        }
    }

    public async Task SaveClipAsync() => await _session.SaveClipAsync();

    public async Task SaveScreenshotAsync() => await _session.SaveScreenshotAsync();

    private string? SetHotkey(string name, string text)
    {
        Action action = name switch
        {
            SettingsViewModel.SaveClipHotkeyName => () => _ = _session.SaveClipAsync(),
            SettingsViewModel.ScreenshotHotkeyName => () => _ = _session.SaveScreenshotAsync(),
            _ => () => _dispatcher.InvokeAsync(() => ToggleRecordingCommand.Execute(null)),
        };
        return _hotkeys.Set(name, text, action);
    }

    private void OnClipSaved(ClipSavedInfo info)
    {
        if (_store.Current.PlaySoundOnClip)
        {
            _sounds.Play(_store.Current);
        }

        _toastPath = info.Path;
        ToastMessage = $"Clipe de {info.Game} salvo";
        _toastTimer.Stop();
        _toastTimer.Start();
        Clips.ScheduleRefresh();
        ClipSaved?.Invoke(info);
    }

    private void OnScreenshotSaved(ClipSavedInfo info)
    {
        var config = _store.Current;
        if (config.PlaySoundOnScreenshot)
        {
            _sounds.PlayScreenshot(config);
        }

        if (config.CopyScreenshotToClipboard)
        {
            CopyImageToClipboard(info.Path);
        }

        _toastPath = info.Path;
        ToastMessage = $"Captura de tela de {info.Game} salva";
        _toastTimer.Stop();
        _toastTimer.Start();
        Clips.ScheduleRefresh();
        ScreenshotSaved?.Invoke(info);
    }

    /// <summary>Puts the screenshot on the clipboard so it can be pasted straight into Discord, like Medal does.</summary>
    private static void CopyImageToClipboard(string path)
    {
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            Clipboard.SetImage(image);
            Log.Info("Captura de tela copiada para a área de transferência.");
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Warn($"Não foi possível copiar a captura para a área de transferência: {ex.Message}");
        }
    }

    private void UpdateGame(DetectedGame? game)
    {
        CurrentGame = game is null ? "Nenhum jogo detectado" : game.DisplayName;
        OnPropertyChanged(nameof(TrayText));
        OnPropertyChanged(nameof(IsGameOpen));
        OnPropertyChanged(nameof(GameActionText));
        UpdateGameStats();
    }

    private void UpdateGameStats()
    {
        var library = Clips.Library.Where(c => !c.IsScreenshot).ToList();
        if (_session.CurrentGame is { } game)
        {
            var folder = ClipPathBuilder.SanitizeFolderName(game.DisplayName);
            var ofGame = library.Where(c => string.Equals(c.Game, folder, StringComparison.OrdinalIgnoreCase)).ToList();
            GameStatValue = ofGame.Count.ToString(CultureInfo.CurrentCulture);
            GameStatLabel = ofGame.Count == 1 ? "clipe deste jogo" : "clipes deste jogo";
            GameStat2Value = ofGame.Count == 0 ? "Nenhum" : Ago(ofGame.Max(c => c.Created));
            GameStat2Label = "último clipe";
        }
        else
        {
            GameStatValue = library.Count.ToString(CultureInfo.CurrentCulture);
            GameStatLabel = library.Count == 1 ? "clipe salvo" : "clipes salvos";
            GameStat2Value = ClipItemViewModel.FormatSize(Clips.Library.Sum(c => c.SizeBytes));
            GameStat2Label = "ocupados na pasta";
        }

        OnPropertyChanged(nameof(GameStatValue));
        OnPropertyChanged(nameof(GameStatLabel));
        OnPropertyChanged(nameof(GameStat2Value));
        OnPropertyChanged(nameof(GameStat2Label));
    }

    /// <summary>"agora", "há 5 min", "há 2 h", "ontem", "há 3 dias" or the date.</summary>
    private static string Ago(DateTime time)
    {
        var elapsed = DateTime.Now - time;
        return elapsed.TotalMinutes < 1 ? "Agora"
            : elapsed.TotalHours < 1 ? $"Há {(int)elapsed.TotalMinutes} min"
            : elapsed.TotalDays < 1 ? $"Há {(int)elapsed.TotalHours} h"
            : elapsed.TotalDays < 2 ? "Ontem"
            : elapsed.TotalDays < 7 ? $"Há {(int)elapsed.TotalDays} dias"
            : time.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture);
    }

    private void UpdateTracks()
    {
        var tracks = _session.Tracks;
        TracksText = tracks.Count == 0 ? "" : string.Join("  ·  ", tracks.Select(t => t.Title));
    }

    private void OnLogged(LogEntry entry)
    {
        _dispatcher.InvokeAsync(() =>
        {
            LogLines.Insert(0, new LogLine(entry.Time.ToString("HH:mm:ss"), entry.Message, entry.Level));
            while (LogLines.Count > MaxLogLines)
            {
                LogLines.RemoveAt(LogLines.Count - 1);
            }
        });
    }

    public void Dispose()
    {
        Log.Logged -= OnLogged;
        _toastTimer.Stop();
        _voice.Dispose();
    }
}
