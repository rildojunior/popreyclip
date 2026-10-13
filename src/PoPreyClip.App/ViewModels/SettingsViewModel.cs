using System.Windows.Input;
using Microsoft.Win32;
using PoPreyClip.Clips;
using PoPreyClip.Config;
using PoPreyClip.Library;

namespace PoPreyClip.App.ViewModels;

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class SettingsViewModel : ObservableObject
{
    public const string SaveClipHotkeyName = "save-clip";
    public const string ToggleRecordingHotkeyName = "toggle-recording";
    public const string ScreenshotHotkeyName = "screenshot";

    private readonly ConfigStore _store;
    private readonly Func<string, string, string?> _applyHotkey;
    private readonly ClipSoundPlayer _sounds;
    private string _clipFolderText;
    private string? _saveHotkeyError;
    private string? _toggleHotkeyError;
    private string? _screenshotHotkeyError;

    /// <param name="applyHotkey">Registers a hotkey by name and returns an error message, or null.</param>
    public SettingsViewModel(ConfigStore store, Func<string, string, string?> applyHotkey, ClipSoundPlayer sounds)
    {
        _sounds = sounds;
        Watched = new WatchedGamesViewModel(store);
        PreviewSoundCommand = new RelayCommand(() => _sounds.Play(C.ClipSound, C.ClipSoundFile, C.ClipSoundVolume));
        BrowseSoundCommand = new RelayCommand(BrowseSound);
        _store = store;
        _applyHotkey = applyHotkey;
        _clipFolderText = store.Current.ClipFolder;
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        OpenFolderCommand = new RelayCommand(() => ClipLibrary.OpenFolder(_store.Current.ClipFolder));
        ApplyFolderCommand = new RelayCommand(() => ApplyFolder(ClipFolderText));
        OpenConfigFileCommand = new RelayCommand(() => ClipLibrary.Play(_store.FilePath));
        OpenLogsCommand = new RelayCommand(() => ClipLibrary.OpenFolder(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoPreyClip", "logs")));
        Monitors = System.Windows.Forms.Screen.AllScreens
            .Select((s, i) => new Option<int>(i, $"Monitor {i + 1}{(s.Primary ? " (principal)" : "")} · {s.Bounds.Width}x{s.Bounds.Height}"))
            .ToList();
    }

    public ICommand BrowseFolderCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand ApplyFolderCommand { get; }

    public ICommand OpenConfigFileCommand { get; }

    public ICommand OpenLogsCommand { get; }

    private AppConfig C => _store.Current;

    // ---- Clip folder ----

    public string ClipFolderText
    {
        get => _clipFolderText;
        set => SetProperty(ref _clipFolderText, value);
    }

    public string FolderExample => Path.Combine(C.ClipFolder, ClipPathBuilder.ClipsSubfolder, "Nome do Jogo",
        $"{ClipPathBuilder.CompactName(C.FilePrefix)}NomeDoJogo{DateTime.Now:yyyyMMddHHmmssfff}.mp4");

    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha a pasta raiz dos clipes",
            InitialDirectory = Directory.Exists(C.ClipFolder) ? C.ClipFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        if (dialog.ShowDialog() == true)
        {
            ApplyFolder(dialog.FolderName);
        }
    }

    private void ApplyFolder(string folder)
    {
        folder = folder?.Trim() ?? "";
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            Log.Warn("Informe um caminho completo, por exemplo D:\\Clipes.");
            ClipFolderText = C.ClipFolder;
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Não foi possível usar a pasta {folder}: {ex.Message}");
            ClipFolderText = C.ClipFolder;
            return;
        }

        _store.Update(c => c.ClipFolder = folder);
        ClipFolderText = folder;
        OnPropertyChanged(nameof(FolderExample));
        Log.Info($"Novos clipes serão salvos em {Path.Combine(folder, ClipPathBuilder.ClipsSubfolder)}\\<Jogo>");
    }

    // ---- Clip ----

    public int ClipSeconds
    {
        get => C.ClipSeconds;
        set
        {
            var rounded = (int)(Math.Round(value / 5.0) * 5);
            _store.Update(c => c.ClipSeconds = rounded);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ClipSecondsText));
        }
    }

    public string ClipSecondsText => C.ClipSeconds >= 60 && C.ClipSeconds % 60 == 0
        ? $"{C.ClipSeconds / 60} min"
        : C.ClipSeconds > 60 ? $"{C.ClipSeconds / 60} min {C.ClipSeconds % 60} s" : $"{C.ClipSeconds} s";

    // ---- Hotkeys ----

    public string SaveClipHotkey
    {
        get => C.Hotkey;
        set
        {
            SaveHotkeyError = ApplyHotkey(SaveClipHotkeyName, value, required: true, (c, v) => c.Hotkey = v);
            OnPropertyChanged();
        }
    }

    public string ToggleRecordingHotkey
    {
        get => C.ToggleRecordingHotkey;
        set
        {
            ToggleHotkeyError = ApplyHotkey(ToggleRecordingHotkeyName, value, required: false, (c, v) => c.ToggleRecordingHotkey = v);
            OnPropertyChanged();
        }
    }

    public string ScreenshotHotkey
    {
        get => C.ScreenshotHotkey;
        set
        {
            ScreenshotHotkeyError = ApplyHotkey(ScreenshotHotkeyName, value, required: false, (c, v) => c.ScreenshotHotkey = v);
            OnPropertyChanged();
        }
    }

    public string? SaveHotkeyError
    {
        get => _saveHotkeyError;
        set => SetProperty(ref _saveHotkeyError, value);
    }

    public string? ToggleHotkeyError
    {
        get => _toggleHotkeyError;
        set => SetProperty(ref _toggleHotkeyError, value);
    }

    public string? ScreenshotHotkeyError
    {
        get => _screenshotHotkeyError;
        set => SetProperty(ref _screenshotHotkeyError, value);
    }

    /// <summary>Validates that the combination is not used by another action, registers it and saves it.</summary>
    private string? ApplyHotkey(string name, string? value, bool required, Action<AppConfig, string> save)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0 && required)
        {
            return "O atalho de salvar clipe é obrigatório.";
        }

        if (value.Length > 0)
        {
            var others = new (string Name, string Hotkey, string Action)[]
            {
                (SaveClipHotkeyName, C.Hotkey, "salvar clipes"),
                (ToggleRecordingHotkeyName, C.ToggleRecordingHotkey, "iniciar ou parar a gravação"),
                (ScreenshotHotkeyName, C.ScreenshotHotkey, "capturar a tela"),
            };
            var conflict = others.FirstOrDefault(o => o.Name != name && value.Equals(o.Hotkey, StringComparison.OrdinalIgnoreCase));
            if (conflict.Name is not null)
            {
                return $"Esse atalho já é usado para {conflict.Action}.";
            }
        }

        var error = _applyHotkey(name, value);
        if (error is null)
        {
            _store.Update(c => save(c, value));
        }

        return error;
    }

    // ---- Screenshots ----

    public bool PlaySoundOnScreenshot
    {
        get => C.PlaySoundOnScreenshot;
        set => _store.Update(c => c.PlaySoundOnScreenshot = value);
    }

    public bool CopyScreenshotToClipboard
    {
        get => C.CopyScreenshotToClipboard;
        set => _store.Update(c => c.CopyScreenshotToClipboard = value);
    }

    public string ScreenshotFolderExample => Path.Combine(C.ClipFolder, ClipPathBuilder.ScreenshotsSubfolder, "Nome do Jogo",
        $"{ClipPathBuilder.CompactName(C.FilePrefix)}NomeDoJogo{DateTime.Now:yyyyMMddHHmmssfff}.png");

    // ---- Video ----

    public IReadOnlyList<Option<int>> FpsOptions { get; } =
        [new(30, "30 FPS"), new(60, "60 FPS"), new(120, "120 FPS"), new(144, "144 FPS")];

    public IReadOnlyList<Option<string>> EncoderOptions { get; } =
    [
        new("auto", "Automático (recomendado)"),
        new("h264_nvenc", "NVIDIA H.264"),
        new("hevc_nvenc", "NVIDIA H.265 / HEVC (arquivos menores)"),
        new("av1_nvenc", "NVIDIA AV1 (RTX 40 ou mais nova)"),
        new("h264_amf", "AMD H.264 (experimental)"),
        new("h264_qsv", "Intel Quick Sync H.264 (experimental)"),
        new("libx264", "Processador (CPU) H.264"),
    ];

    public IReadOnlyList<Option<int>> Monitors { get; }

    public Option<int>? Fps
    {
        get => FpsOptions.FirstOrDefault(o => o.Value == C.Video.Fps) ?? FpsOptions[1];
        set
        {
            if (value is not null)
            {
                _store.Update(c => c.Video.Fps = value.Value);
            }
        }
    }

    public Option<string>? Encoder
    {
        get => EncoderOptions.FirstOrDefault(o => o.Value.Equals(C.Video.Encoder, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
            {
                _store.Update(c => c.Video.Encoder = value.Value);
            }
        }
    }

    public Option<int>? Monitor
    {
        get => Monitors.FirstOrDefault(o => o.Value == C.Video.MonitorIndex) ?? Monitors.FirstOrDefault();
        set
        {
            if (value is not null)
            {
                _store.Update(c => c.Video.MonitorIndex = value.Value);
            }
        }
    }

    /// <summary>Shown as "quality" (higher is better) while the config stores CQ (lower is better).</summary>
    public int Quality
    {
        get => 52 - C.Video.Quality;
        set
        {
            _store.Update(c => c.Video.Quality = Math.Clamp(52 - value, 15, 40));
            OnPropertyChanged();
            OnPropertyChanged(nameof(QualityText));
        }
    }

    public string QualityText => C.Video.Quality switch
    {
        <= 18 => "Máxima (arquivos grandes)",
        <= 22 => "Alta",
        <= 27 => "Boa",
        <= 32 => "Média",
        _ => "Baixa (arquivos pequenos)",
    };

    public bool ShowCursor
    {
        get => C.Video.ShowCursor;
        set => _store.Update(c => c.Video.ShowCursor = value);
    }

    // ---- Behavior ----

    public bool StartRecordingOnLaunch
    {
        get => C.StartRecordingOnLaunch;
        set => _store.Update(c => c.StartRecordingOnLaunch = value);
    }

    /// <summary>Idle mode: record only while a game or watched app is open.</summary>
    public bool AutoRecordGames
    {
        get => C.AutoRecordGames;
        set
        {
            _store.Update(c => c.AutoRecordGames = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanChooseStartOnLaunch));
        }
    }

    /// <summary>"Começar a gravar ao abrir" only matters when idle mode is off.</summary>
    public bool CanChooseStartOnLaunch => !C.AutoRecordGames;

    /// <summary>Games and watched apps list (shared card in the settings page).</summary>
    public WatchedGamesViewModel Watched { get; }

    public bool MinimizeToTray
    {
        get => C.MinimizeToTray;
        set => _store.Update(c => c.MinimizeToTray = value);
    }

    public bool PlaySoundOnClip
    {
        get => C.PlaySoundOnClip;
        set
        {
            _store.Update(c => c.PlaySoundOnClip = value);
            OnPropertyChanged();
        }
    }

    // ---- Clip saved sound ----

    public ICommand PreviewSoundCommand { get; }

    public ICommand BrowseSoundCommand { get; }

    public IReadOnlyList<Option<string>> SoundOptions { get; } =
        ClipSoundPlayer.BuiltIn.Select(s => new Option<string>(s.Id, s.Label))
            .Append(new Option<string>(ClipSoundPlayer.CustomId, "Arquivo meu (.wav ou .mp3)"))
            .ToList();

    public Option<string>? Sound
    {
        get => SoundOptions.FirstOrDefault(o => o.Value == C.ClipSound) ?? SoundOptions[0];
        set
        {
            if (value is null)
            {
                return;
            }

            _store.Update(c => c.ClipSound = value.Value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomSound));
            if (value.Value == ClipSoundPlayer.CustomId && string.IsNullOrWhiteSpace(C.ClipSoundFile))
            {
                BrowseSound();
            }
            else
            {
                _sounds.Play(C.ClipSound, C.ClipSoundFile, C.ClipSoundVolume);
            }
        }
    }

    public bool IsCustomSound => C.ClipSound == ClipSoundPlayer.CustomId;

    public string CustomSoundName => string.IsNullOrWhiteSpace(C.ClipSoundFile) ? "Nenhum arquivo escolhido" : Path.GetFileName(C.ClipSoundFile);

    public int SoundVolume
    {
        get => C.ClipSoundVolume;
        set
        {
            _store.Update(c => c.ClipSoundVolume = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SoundVolumeText));
        }
    }

    public string SoundVolumeText => $"{C.ClipSoundVolume}%";

    private void BrowseSound()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Escolha o som do clipe",
            Filter = "Áudio (*.wav;*.mp3)|*.wav;*.mp3|Todos os arquivos (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _store.Update(c =>
        {
            c.ClipSound = ClipSoundPlayer.CustomId;
            c.ClipSoundFile = dialog.FileName;
        });
        OnPropertyChanged(nameof(Sound));
        OnPropertyChanged(nameof(IsCustomSound));
        OnPropertyChanged(nameof(CustomSoundName));
        _sounds.Play(C.ClipSound, C.ClipSoundFile, C.ClipSoundVolume);
    }

    public bool DetectFullscreenApps
    {
        get => C.DetectFullscreenApps;
        set => _store.Update(c => c.DetectFullscreenApps = value);
    }

    public string ConfigPath => _store.FilePath;

    /// <summary>Update checks shown in the "Atualizações" card (shared with the banner).</summary>
    public UpdatesViewModel? Updates { get; init; }
}
