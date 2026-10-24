using System.Diagnostics;
using System.Net.Http;
using System.Windows.Input;
using System.Windows.Threading;
using PoPreyClip.Config;
using PoPreyClip.Updates;

namespace PoPreyClip.App.ViewModels;

/// <summary>Checks GitHub for new versions, shows the banner, downloads and installs updates.</summary>
public sealed class UpdatesViewModel : ObservableObject
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    /// <summary>Opening the window checks again only when the last check is older than this.</summary>
    private static readonly TimeSpan WindowCheckMinimumGap = TimeSpan.FromMinutes(15);

    /// <summary>A dismissed banner comes back after this long, even for the same version.</summary>
    private static readonly TimeSpan DismissDuration = TimeSpan.FromHours(24);

    private readonly ConfigStore _store;
    private readonly Action _exitForUpdate;
    private readonly UpdateService _service = new();
    private readonly DispatcherTimer _timer;
    private UpdateInfo? _available;
    private Version? _dismissedVersion;
    private DateTime _dismissedAt;
    private DateTime _lastCheck = DateTime.MinValue;
    private bool _checking;
    private bool _isBusy;
    private double _progress;
    private string? _status;

    public UpdatesViewModel(ConfigStore store, Action exitForUpdate)
    {
        _store = store;
        _exitForUpdate = exitForUpdate;
        CheckNowCommand = new AsyncRelayCommand(() => CheckAsync(manual: true), () => !IsBusy);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => Available is not null && !IsBusy);
        OpenReleasePageCommand = new RelayCommand(() => OpenUrl(Available?.PageUrl ?? UpdateService.ReleasesPage));
        OpenRepositoryCommand = new RelayCommand(() => OpenUrl(UpdateService.RepositoryPage));
        OpenChangelogCommand = new RelayCommand(() => OpenUrl(UpdateService.ChangelogPage));
        DismissCommand = new RelayCommand(() =>
        {
            _dismissedVersion = Available?.Version;
            _dismissedAt = DateTime.UtcNow;
            OnPropertyChanged(nameof(IsBannerVisible));
        });
        _timer = new DispatcherTimer(FirstCheckDelay, DispatcherPriority.Background, async (_, _) =>
        {
            _timer!.Interval = CheckInterval;
            if (_store.Current.AutoCheckUpdates && !UpdateService.IsTestBuild)
            {
                await CheckAsync(manual: false);
                await UpdateFfmpegAsync(manual: false);
            }
        }, Dispatcher.CurrentDispatcher);

        var dispatcher = Dispatcher.CurrentDispatcher;
        FfmpegManager.Changed += () => dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(FfmpegText));
            OnPropertyChanged(nameof(FfmpegStatus));
        });
    }

    /// <summary>Which FFmpeg the recordings use.</summary>
    public string FfmpegText => FfmpegManager.InstalledVersion is { } version
        ? $"FFmpeg {version.ToString(3)}"
        : File.Exists(_store.Current.FfmpegExe) ? "FFmpeg instalado no PC" : "FFmpeg não encontrado";

    public string FfmpegStatus => FfmpegManager.Progress is { } progress
        ? $"Baixando o FFmpeg... {progress:P0}"
        : _ffmpegStatus ?? (FfmpegManager.InstalledVersion is null
            ? "O PoPreyClip baixa o FFmpeg (cerca de 110 MB) e passa a mantê-lo atualizado sozinho."
            : "Usado para gravar. É atualizado sozinho junto com o app.");

    private string? _ffmpegStatus;

    /// <summary>
    /// Downloads FFmpeg when PoPreyClip has no copy of its own yet, or a newer version exists. Runs in the
    /// background; a failure (offline) is retried at the next check.
    /// </summary>
    private async Task UpdateFfmpegAsync(bool manual)
    {
        try
        {
            var installed = await Task.Run(() => FfmpegManager.UpdateAsync());
            _ffmpegStatus = installed is not null ? $"FFmpeg atualizado para a versão {installed.ToString(3)}." : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException or InvalidDataException or UnauthorizedAccessException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"Não foi possível baixar o FFmpeg: {ex.Message}");
            _ffmpegStatus = manual || FfmpegManager.InstalledVersion is null
                ? "Não foi possível baixar o FFmpeg agora. O PoPreyClip tenta de novo mais tarde."
                : null;
        }

        OnPropertyChanged(nameof(FfmpegText));
        OnPropertyChanged(nameof(FfmpegStatus));
    }

    /// <summary>Raised when a newer version is found, so the tray can show a notification.</summary>
    public event Action<UpdateInfo>? UpdateFound;

    public ICommand CheckNowCommand { get; }

    public ICommand InstallCommand { get; }

    public ICommand OpenReleasePageCommand { get; }

    public ICommand OpenRepositoryCommand { get; }

    public ICommand OpenChangelogCommand { get; }

    /// <summary>Shown under the GitHub buttons, e.g. "github.com/rildojunior/popreyclip".</summary>
    public string RepositoryText => UpdateService.RepositoryPage.Replace("https://", "", StringComparison.Ordinal);

    public ICommand DismissCommand { get; }

    public string CurrentVersionText => UpdateService.IsTestBuild
        ? $"Versão {UpdateService.CurrentVersion.ToString(3)} (app de testes: não procura atualizações sozinho)"
        : $"Versão {UpdateService.CurrentVersion.ToString(3)}";

    public UpdateInfo? Available
    {
        get => _available;
        private set
        {
            if (SetProperty(ref _available, value))
            {
                OnPropertyChanged(nameof(IsBannerVisible));
                OnPropertyChanged(nameof(AvailableText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string AvailableText => Available is null ? "" : $"Nova versão {Available.Version.ToString(3)} disponível";

    /// <summary>
    /// "Lembrar depois" hides the banner for 24 hours; it comes back sooner when a newer version than the dismissed
    /// one comes out.
    /// </summary>
    public bool IsBannerVisible => Available is not null
        && !(Available.Version == _dismissedVersion && DateTime.UtcNow - _dismissedAt < DismissDuration);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(InstallText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Download progress, 0 to 1.</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value))
            {
                OnPropertyChanged(nameof(InstallText));
            }
        }
    }

    public string InstallText => IsBusy ? $"Baixando {Progress:P0}" : "Atualizar agora";

    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool AutoCheck
    {
        get => _store.Current.AutoCheckUpdates;
        set
        {
            _store.Update(c => c.AutoCheckUpdates = value);
            OnPropertyChanged();
        }
    }

    public void Start()
    {
        _timer.Start();

        // Without its own FFmpeg the app can't record well (or at all): get it right away, even with automatic
        // update checks turned off.
        if (FfmpegManager.InstalledVersion is null)
        {
            _ = UpdateFfmpegAsync(manual: false);
        }
        else
        {
            FfmpegManager.RemoveOldVersions();
        }
    }

    /// <summary>Called when the window is opened: checks again when the last check is more than 15 minutes old.</summary>
    public void CheckIfStale()
    {
        // Before the first check (8 seconds after start-up) there is nothing to refresh yet.
        if (_store.Current.AutoCheckUpdates && !UpdateService.IsTestBuild && _lastCheck != DateTime.MinValue && DateTime.UtcNow - _lastCheck >= WindowCheckMinimumGap)
        {
            _ = CheckAsync(manual: false);
        }
    }

    public async Task CheckAsync(bool manual)
    {
        if (manual)
        {
            _ = UpdateFfmpegAsync(manual: true);
        }

        if (IsBusy || _checking)
        {
            return;
        }

        if (manual)
        {
            Status = "Procurando atualizações...";
        }

        _checking = true;
        _lastCheck = DateTime.UtcNow;
        try
        {
            var update = await _service.CheckAsync();
            if (update is null)
            {
                Status = $"Você já está na versão mais nova ({UpdateService.CurrentVersion.ToString(3)}).";
                return;
            }

            var isNew = Available?.Version != update.Version;
            Available = update;
            Status = $"Nova versão {update.Version.ToString(3)} disponível.";
            if (manual)
            {
                _dismissedVersion = null;
            }

            // Also brings the banner back once the 24 hours of "Lembrar depois" are over.
            OnPropertyChanged(nameof(IsBannerVisible));

            if (isNew)
            {
                Log.Info($"Nova versão disponível: {update.Version.ToString(3)} (atual {UpdateService.CurrentVersion.ToString(3)}).");
                UpdateFound?.Invoke(update);
            }

            // Test hook: install without a click (used by the automated update test only).
            if (Environment.GetEnvironmentVariable("POPREYCLIP_AUTO_INSTALL_UPDATE") == "1")
            {
                await InstallAsync();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException or KeyNotFoundException or InvalidOperationException)
        {
            // Offline or GitHub unavailable: try again at the next interval; only tell the user when they asked.
            Log.Debug($"Não foi possível verificar atualizações: {ex.Message}");
            if (manual)
            {
                Status = "Não foi possível verificar agora. Confira sua conexão com a internet.";
            }
        }
        finally
        {
            _checking = false;
        }
    }

    private async Task InstallAsync()
    {
        if (Available is not { } update)
        {
            return;
        }

        if (!UpdateService.CanSelfUpdate)
        {
            // Development build or read-only folder: let the user download it by hand.
            OpenUrl(update.PageUrl);
            Status = "Baixe a nova versão na página que abriu e substitua o arquivo PoPreyClip.exe.";
            return;
        }

        IsBusy = true;
        Progress = 0;
        try
        {
            var file = await _service.DownloadAsync(update, new Progress<double>(p => Progress = p));
            Status = "Instalando a atualização...";
            UpdateService.StartInstaller(file, App.ForwardArguments);
            _exitForUpdate();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Não foi possível atualizar: {ex.Message}");
            Status = $"Não foi possível atualizar: {ex.Message}";
            IsBusy = false;
        }
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
