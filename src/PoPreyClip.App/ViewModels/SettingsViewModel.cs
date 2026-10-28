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

/// <summary>A resolution or bitrate in the list. <see cref="Tone"/> colors heavy choices, like Medal.</summary>
public sealed record VideoChoice(int Value, string Label, string Tone = "")
{
    public const string Warning = "warning";
    public const string Danger = "danger";

    public override string ToString() => Label;
}

/// <summary>A group of settings cards, listed under "Configurações" in the sidebar.</summary>
public sealed class SettingsSection(string id, string title, Action<string> go) : ObservableObject
{
    private bool _isCurrent;

    public string Id { get; } = id;

    public string Title { get; } = title;

    public ICommand GoCommand { get; } = new RelayCommand(() => go(id));

    /// <summary>The group shown at the top of the settings page right now.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
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
    private string? _currentSection;

    /// <param name="applyHotkey">Registers a hotkey by name and returns an error message, or null.</param>
    public SettingsViewModel(ConfigStore store, Func<string, string, string?> applyHotkey, ClipSoundPlayer sounds)
    {
        _sounds = sounds;
        Watched = new WatchedGamesViewModel(store);
        PreviewSoundCommand = new RelayCommand(() => _sounds.Play(C.ClipSound, C.ClipSoundFile, C.ClipSoundVolume));
        BrowseSoundCommand = new RelayCommand(BrowseSound);
        PreviewScreenshotSoundCommand = new RelayCommand(() => _sounds.PlayScreenshot(C));
        TestNoticeCommand = new RelayCommand(() => NoticeTestRequested?.Invoke(), () => C.SavedNotice != AppConfig.NoticeOff);
        _store = store;
        _applyHotkey = applyHotkey;
        _clipFolderText = store.Current.ClipFolder;
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        OpenFolderCommand = new RelayCommand(() => ClipLibrary.OpenFolder(_store.Current.ClipFolder));
        ApplyFolderCommand = new RelayCommand(() => ApplyFolder(ClipFolderText));
        OpenConfigFileCommand = new RelayCommand(() => ClipLibrary.Play(_store.FilePath));
        OpenLogsCommand = new RelayCommand(() => ClipLibrary.OpenFolder(Log.LogDirectory));
        Sections =
        [
            new("geral", "Geral", GoToSection),
            new("captura", "Captura", GoToSection),
            new("atalhos", "Atalhos e voz", GoToSection),
            new("avisos", "Avisos e sons", GoToSection),
            new("jogos", "Jogos", GoToSection),
            new("atualizacoes", "Atualizações", GoToSection),
        ];
        CurrentSection = Sections[0].Id;
        Monitors = System.Windows.Forms.Screen.AllScreens
            .Select((s, i) => new Option<int>(i, $"Monitor {i + 1}{(s.Primary ? " (principal)" : "")} · {s.Bounds.Width}x{s.Bounds.Height}"))
            .ToList();
    }

    // ---- Sections ----

    /// <summary>The six groups of the page, listed in the sidebar while the settings page is open.</summary>
    public IReadOnlyList<SettingsSection> Sections { get; }

    /// <summary>Asks the page to scroll to a group (the sidebar list was clicked).</summary>
    public event Action<string>? ScrollToSectionRequested;

    /// <summary>Group at the top of the page; highlighted in the sidebar. The page updates it while scrolling.</summary>
    public string? CurrentSection
    {
        get => _currentSection;
        set
        {
            if (SetProperty(ref _currentSection, value))
            {
                foreach (var section in Sections)
                {
                    section.IsCurrent = section.Id == value;
                }
            }
        }
    }

    private void GoToSection(string id)
    {
        CurrentSection = id;
        SectionNavigationRequested?.Invoke();
        ScrollToSectionRequested?.Invoke(id);
    }

    /// <summary>Raised before scrolling to a group, so the window can switch to the settings page.</summary>
    public event Action? SectionNavigationRequested;

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
                (ToggleRecordingHotkeyName, C.ToggleRecordingHotkey, "ativar ou desativar a captura de clipes"),
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
        set
        {
            _store.Update(c => c.PlaySoundOnScreenshot = value);
            OnPropertyChanged();
        }
    }

    public ICommand PreviewScreenshotSoundCommand { get; }

    public IReadOnlyList<Option<string>> ScreenshotSoundOptions { get; } =
        ClipSoundPlayer.ScreenshotSounds.Select(s => new Option<string>(s.Id, s.Label)).ToList();

    public Option<string>? ScreenshotSound
    {
        get => ScreenshotSoundOptions.FirstOrDefault(o => o.Value == C.ScreenshotSound) ?? ScreenshotSoundOptions[0];
        set
        {
            if (value is null)
            {
                return;
            }

            _store.Update(c => c.ScreenshotSound = value.Value);
            OnPropertyChanged();
            _sounds.PlayScreenshot(C);
        }
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
        [new(24, "24 FPS"), new(30, "30 FPS"), new(60, "60 FPS"), new(120, "120 FPS"), new(144, "144 FPS")];

    /// <summary>Clip sizes, like Medal's list. 2K and 4K are marked red: they weigh a lot on the PC and the files.</summary>
    public IReadOnlyList<VideoChoice> ResolutionOptions { get; } =
    [
        new(2160, "UHD 4K (2160p)", VideoChoice.Danger),
        new(1440, "QHD 2K (1440p)", VideoChoice.Danger),
        new(1080, "Full HD (1080p)"),
        new(720, "Alto (720p)"),
        new(480, "Médio (480p)"),
        new(360, "Baixo (360p)"),
    ];

    /// <summary>Bitrates, like Medal's list: from 30M up in yellow, from 70M up in red (very big files).</summary>
    public IReadOnlyList<VideoChoice> BitrateOptions { get; } = VideoPresets.Bitrates
        .Select(b => new VideoChoice(b, $"{b}M", b >= 70 ? VideoChoice.Danger : b >= 30 ? VideoChoice.Warning : ""))
        .ToList();

    public IReadOnlyList<Option<string>> EncoderOptions { get; } =
    [
        new("auto", "Automático (recomendado)"),
        new("h264_nvenc", "NVIDIA H.264"),
        new("hevc_nvenc", "NVIDIA H.265 / HEVC (arquivos menores)"),
        new("av1_nvenc", "NVIDIA AV1 (RTX 40 ou mais nova)"),
        new("h264_amf", "AMD H.264"),
        new("h264_qsv", "Intel Quick Sync H.264 (experimental)"),
        new("libx264", "Processador (CPU) H.264"),
    ];

    public IReadOnlyList<Option<int>> Monitors { get; }

    public IReadOnlyList<Option<string>> CaptureOptions { get; } =
    [
        new(VideoConfig.CaptureGameWindow, "Só a janela do jogo (recomendado)"),
        new(VideoConfig.CaptureMonitor, "Monitor inteiro"),
    ];

    public Option<string>? Capture
    {
        get => CaptureOptions.FirstOrDefault(o => o.Value == C.Video.Capture) ?? CaptureOptions[0];
        set
        {
            if (value is not null)
            {
                _store.Update(c => c.Video.Capture = value.Value);
            }
        }
    }

    public Option<int>? Fps
    {
        get => FpsOptions.FirstOrDefault(o => o.Value == C.Video.Fps) ?? FpsOptions[1];
        set
        {
            if (value is not null)
            {
                _store.Update(c => c.Video.Fps = value.Value);
                NotifyVideoQuality();
            }
        }
    }

    public VideoChoice? Resolution
    {
        get => ResolutionOptions.FirstOrDefault(o => o.Value == C.Video.Resolution);
        set
        {
            if (value is not null)
            {
                _store.Update(c => c.Video.Resolution = value.Value);
                NotifyVideoQuality();
            }
        }
    }

    public VideoChoice? Bitrate
    {
        get => BitrateOptions.FirstOrDefault(o => o.Value == C.Video.BitrateMbps);
        set
        {
            if (value is not null)
            {
                _store.Update(c => c.Video.BitrateMbps = value.Value);
                NotifyVideoQuality();
            }
        }
    }

    // One flag per preset card. Picking Custom keeps the current values so they can be changed one by one.
    public bool IsLowPreset
    {
        get => C.Video.Preset == VideoPresets.Low;
        set => PickPreset(value, VideoPresets.Low);
    }

    public bool IsStandardPreset
    {
        get => C.Video.Preset == VideoPresets.Standard;
        set => PickPreset(value, VideoPresets.Standard);
    }

    public bool IsHighPreset
    {
        get => C.Video.Preset == VideoPresets.High;
        set => PickPreset(value, VideoPresets.High);
    }

    public bool IsCustomPreset
    {
        get => C.Video.Preset == VideoPresets.Custom;
        set => PickPreset(value, VideoPresets.Custom);
    }

    /// <summary>What the chosen settings record, e.g. "1080p · 60 FPS · 15 Mbps".</summary>
    public string VideoQualitySummary =>
        $"{C.Video.Resolution}p · {C.Video.Fps} FPS · {C.Video.BitrateMbps} Mbps. Um jogo ou monitor menor que isso é gravado no tamanho dele, sem ampliar.";

    private void PickPreset(bool picked, string preset)
    {
        if (!picked || C.Video.Preset == preset)
        {
            return;
        }

        _store.Update(c => VideoPresets.Apply(c.Video, preset));
        NotifyVideoQuality();
    }

    private void NotifyVideoQuality()
    {
        OnPropertyChanged(nameof(Fps));
        OnPropertyChanged(nameof(Resolution));
        OnPropertyChanged(nameof(Bitrate));
        OnPropertyChanged(nameof(IsLowPreset));
        OnPropertyChanged(nameof(IsStandardPreset));
        OnPropertyChanged(nameof(IsHighPreset));
        OnPropertyChanged(nameof(IsCustomPreset));
        OnPropertyChanged(nameof(VideoQualitySummary));
    }

    public Option<string>? Encoder
    {
        get => EncoderOptions.FirstOrDefault(o => o.Value.Equals(C.Video.Encoder, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
            {
                // H.265 and AV1 need less bitrate: a preset follows the encoder, like Medal.
                _store.Update(c =>
                {
                    c.Video.Encoder = value.Value;
                    if (c.Video.Preset != VideoPresets.Custom)
                    {
                        VideoPresets.Apply(c.Video, c.Video.Preset);
                    }
                });
                NotifyVideoQuality();
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

    private readonly PoPreyClip.Startup.WindowsStartup _startup = new();

    /// <summary>Lives in the Windows Run key, not in the config file.</summary>
    public bool StartWithWindows
    {
        get => _startup.IsEnabled;
        set
        {
            try
            {
                _startup.SetEnabled(value);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Log.Warn($"Could not change start with Windows: {ex.Message}");
            }

            OnPropertyChanged();
        }
    }

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

    // ---- Saved notice ----

    /// <summary>Raised by the "Testar" button; the tray icon shows a sample notice in the chosen style.</summary>
    public event Action? NoticeTestRequested;

    public ICommand TestNoticeCommand { get; }

    public IReadOnlyList<Option<string>> SavedNoticeOptions { get; } =
    [
        new(AppConfig.NoticeOverlay, "Aviso do PoPreyClip"),
        new(AppConfig.NoticeWindows, "Notificação do Windows"),
        new(AppConfig.NoticeOff, "Nenhum aviso"),
    ];

    public Option<string> SavedNotice
    {
        get => SavedNoticeOptions.FirstOrDefault(o => o.Value == C.SavedNotice) ?? SavedNoticeOptions[0];
        set
        {
            if (value is null)
            {
                return;
            }

            _store.Update(c => c.SavedNotice = value.Value);
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
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

    /// <summary>"Comando de voz" card.</summary>
    public VoiceCommandsViewModel? Voice { get; init; }

    /// <summary>Update checks shown in the "Atualizações" card (shared with the banner).</summary>
    public UpdatesViewModel? Updates { get; init; }
}
