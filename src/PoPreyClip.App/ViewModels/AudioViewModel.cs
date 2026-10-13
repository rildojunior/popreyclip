using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using PoPreyClip.Audio;
using PoPreyClip.Config;
using PoPreyClip.Native;
using PoPreyClip.Session;

namespace PoPreyClip.App.ViewModels;

public enum AudioTrackMode
{
    /// <summary>One track with every source mixed at its volume.</summary>
    Unified,

    /// <summary>A mixed track plus one track per source.</summary>
    SeparateAndUnified,

    /// <summary>Only one track per source.</summary>
    Separate,
}

public sealed record RunningApp(string ExeName, string Description)
{
    public string Display => Description.Length > 0 && !Description.Equals(ExeName, StringComparison.OrdinalIgnoreCase)
        ? $"{Description}  ({ExeName})"
        : ExeName;

    public override string ToString() => Display;
}

public sealed class AudioViewModel : ObservableObject
{
    private readonly ConfigStore _store;
    private readonly RecordingSession _session;
    private readonly DispatcherTimer _meterTimer;
    private RunningApp? _selectedApp;
    private bool _loadingApps;

    public AudioViewModel(ConfigStore store, RecordingSession session)
    {
        _store = store;
        _session = session;
        foreach (var source in store.Current.Audio.Sources)
        {
            Sources.Add(new AudioSourceViewModel(this, source));
        }

        AddAppCommand = new RelayCommand(AddSelectedApp, () => SelectedApp is not null);
        RefreshAppsCommand = new AsyncRelayCommand(LoadAppsAsync);
        RefreshDevicesCommand = new RelayCommand(LoadMicrophones);
        LoadMicrophones();
        _ = LoadAppsAsync();

        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => UpdateMeters(), Dispatcher.CurrentDispatcher);
        _meterTimer.Start();
    }

    public ObservableCollection<AudioSourceViewModel> Sources { get; } = [];

    public ObservableCollection<string> Microphones { get; } = [];

    public ObservableCollection<RunningApp> RunningApps { get; } = [];

    public ICommand AddAppCommand { get; }

    public ICommand RefreshAppsCommand { get; }

    public ICommand RefreshDevicesCommand { get; }

    public RunningApp? SelectedApp
    {
        get => _selectedApp;
        set => SetProperty(ref _selectedApp, value);
    }

    public bool LoadingApps
    {
        get => _loadingApps;
        private set => SetProperty(ref _loadingApps, value);
    }

    public AudioTrackMode Mode
    {
        get
        {
            var audio = _store.Current.Audio;
            if (!audio.MultipleAudioTracks)
            {
                return AudioTrackMode.Unified;
            }

            return audio.IncludeMixTrack ? AudioTrackMode.SeparateAndUnified : AudioTrackMode.Separate;
        }
        set
        {
            if (value == Mode)
            {
                return;
            }

            _store.Update(c =>
            {
                c.Audio.MultipleAudioTracks = value != AudioTrackMode.Unified;
                c.Audio.IncludeMixTrack = value != AudioTrackMode.Separate;
            });
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeDescription));
        }
    }

    public string ModeDescription => Mode switch
    {
        AudioTrackMode.Unified => "O clipe terá uma única faixa com todas as fontes misturadas, cada uma no seu volume.",
        AudioTrackMode.Separate => "O clipe terá uma faixa por fonte, cada uma no seu volume. Players comuns tocam só a primeira faixa.",
        _ => "O clipe terá a faixa \"Mix\" com tudo junto e, depois dela, uma faixa por fonte. Todas usam os volumes abaixo.",
    };

    internal ConfigStore Store => _store;

    internal void UpdateSource(string id, Action<AudioSourceConfig> change) =>
        _store.Update(c =>
        {
            var source = c.Audio.Sources.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (source is not null)
            {
                change(source);
            }
        });

    internal void RemoveSource(AudioSourceViewModel source)
    {
        _store.Update(c => c.Audio.Sources.RemoveAll(s => s.Id.Equals(source.Id, StringComparison.OrdinalIgnoreCase)));
        Sources.Remove(source);
    }

    private void AddSelectedApp()
    {
        if (SelectedApp is not { } app)
        {
            return;
        }

        if (Sources.Any(s => s.Id.Equals(app.ExeName, StringComparison.OrdinalIgnoreCase)))
        {
            Log.Warn($"{app.ExeName} já está na lista de fontes.");
            return;
        }

        var name = app.Description.Length is > 0 and <= 24 ? app.Description : Path.GetFileNameWithoutExtension(app.ExeName);
        var config = new AudioSourceConfig { Id = app.ExeName, Name = name };
        _store.Update(c => c.Audio.Sources.Add(config));
        Sources.Add(new AudioSourceViewModel(this, config));
        SelectedApp = null;
    }

    private void LoadMicrophones()
    {
        Microphones.Clear();
        Microphones.Add("Auto");
        try
        {
            foreach (var device in AudioDevices.ListMicrophones())
            {
                Microphones.Add(device.Name);
            }
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Log.Warn($"Não foi possível listar microfones: {ex.Message}");
        }

        foreach (var source in Sources)
        {
            source.RefreshDevice();
        }
    }

    private async Task LoadAppsAsync()
    {
        LoadingApps = true;
        var apps = await Task.Run(FindRunningApps);
        RunningApps.Clear();
        foreach (var app in apps)
        {
            RunningApps.Add(app);
        }

        LoadingApps = false;
    }

    /// <summary>User programs (not part of Windows) that are running now, one entry per executable.</summary>
    private static List<RunningApp> FindRunningApps()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sessionId = Process.GetCurrentProcess().SessionId;
        var result = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Win32.GetProcesses())
        {
            if (result.ContainsKey(process.ExeName) || process.ProcessId == Environment.ProcessId)
            {
                continue;
            }

            var path = Win32.GetProcessPath(process.ProcessId);
            if (path is null || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var p = Process.GetProcessById(process.ProcessId);
                if (p.SessionId != sessionId)
                {
                    continue;
                }
            }
            catch (ArgumentException)
            {
                continue;
            }

            string description;
            try
            {
                description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? "";
            }
            catch (FileNotFoundException)
            {
                description = "";
            }

            result[process.ExeName] = new RunningApp(process.ExeName, description);
        }

        return result.Values.OrderBy(a => a.Display, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void UpdateMeters()
    {
        foreach (var source in Sources)
        {
            source.PushLevel(_session.TakeLevel(source.Id));
        }
    }
}

public sealed class AudioSourceViewModel : ObservableObject
{
    private readonly AudioViewModel _owner;
    private bool _enabled;
    private int _volume;
    private double _gainDb;
    private string _device;
    private double _level;

    public AudioSourceViewModel(AudioViewModel owner, AudioSourceConfig config)
    {
        _owner = owner;
        Id = config.Id;
        Name = config.Name;
        _enabled = config.Enabled;
        _volume = config.Volume;
        _gainDb = config.GainDb;
        _device = string.IsNullOrWhiteSpace(config.Device) ? "Auto" : config.Device;
        RemoveCommand = new RelayCommand(() => _owner.RemoveSource(this));
        ResetVolumeCommand = new RelayCommand(() => Volume = 100);
    }

    public string Id { get; }

    public string Name { get; }

    public bool IsGame => Id.Equals(AudioSourceIds.GameAudio, StringComparison.OrdinalIgnoreCase);

    public bool IsMicrophone => Id.Equals(AudioSourceIds.Microphone, StringComparison.OrdinalIgnoreCase);

    public bool CanRemove => !IsGame && !IsMicrophone;

    public string Icon => IsGame ? "" : IsMicrophone ? "" : Id.Contains("discord", StringComparison.OrdinalIgnoreCase) ? "" : "";

    public string Description => IsGame
        ? "Somente o som do jogo detectado (e dos processos dele)."
        : IsMicrophone
            ? "Sua voz, pelo microfone escolhido abaixo."
            : $"Somente o som do aplicativo {Id}.";

    public ICommand RemoveCommand { get; }

    public ICommand ResetVolumeCommand { get; }

    public IEnumerable<string> Microphones => _owner.Microphones;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                _owner.UpdateSource(Id, s => s.Enabled = value);
            }
        }
    }

    public int Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(value, 0, 200);
            if (SetProperty(ref _volume, value))
            {
                _owner.UpdateSource(Id, s => s.Volume = value);
                OnPropertyChanged(nameof(VolumeText));
            }
        }
    }

    public string VolumeText => $"{Volume}%";

    /// <summary>Extra amplification for quiet sources, 0 to 30 dB. The limiter keeps it from distorting.</summary>
    public double GainDb
    {
        get => _gainDb;
        set
        {
            value = Math.Round(Math.Clamp(value, 0, AudioSourceLimits.MaxGainDb));
            if (SetProperty(ref _gainDb, value))
            {
                _owner.UpdateSource(Id, s => s.GainDb = value);
                OnPropertyChanged(nameof(GainText));
            }
        }
    }

    public string GainText => GainDb <= 0 ? "Sem reforço" : $"+{GainDb:0} dB";

    public string Device
    {
        get => _device;
        set
        {
            if (value is null || !SetProperty(ref _device, value))
            {
                return;
            }

            _owner.UpdateSource(Id, s => s.Device = value);
        }
    }

    /// <summary>Displayed level, 0 to 1, with a short decay so peaks stay visible.</summary>
    public double Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    internal void PushLevel(float level)
    {
        // Loudness (RMS) on a -54 dB to -6 dB scale: speech and game audio at the same
        // perceived volume now fill the bar to about the same point.
        var target = level <= 0 ? 0 : Math.Clamp((20 * Math.Log10(level) + 54) / 48, 0, 1);
        var next = Math.Max(target, Level - 0.03);
        Level = next < 0.01 ? 0 : next;
    }

    internal void RefreshDevice() => OnPropertyChanged(nameof(Device));
}
