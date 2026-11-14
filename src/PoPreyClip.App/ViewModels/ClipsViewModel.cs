using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PoPreyClip.Clips;
using PoPreyClip.Config;
using PoPreyClip.Library;

namespace PoPreyClip.App.ViewModels;

public enum LibraryKind
{
    All,
    Videos,
    Screenshots,
}

public enum ShareApp
{
    Discord,
    WhatsApp,
}

public sealed record GameFilter(string? Game, string Label, int Count)
{
    public string Display => $"{Label}  {Count}";
}

/// <summary>One row of the clip grid. The grid is a list of rows so that only the rows on screen are created.</summary>
public sealed record ClipRow(IReadOnlyList<ClipItemViewModel> Items, int Columns);

public sealed class ClipsViewModel : ObservableObject
{
    private readonly ConfigStore _store;
    private readonly DispatcherTimer _refreshDebounce;
    private List<ClipItemViewModel> _all = [];
    private FileSystemWatcher? _watcher;
    private string? _watchedRoot;
    private GameFilter? _selectedFilter;
    private string _search = "";
    private LibraryKind _kind = LibraryKind.All;
    private bool _isEmpty = true;
    private int _columns = 1;
    private List<ClipItemViewModel> _visible = [];
    private string _summary = "";

    public ClipsViewModel(ConfigStore store)
    {
        _store = store;
        RefreshCommand = new RelayCommand(Refresh);
        OpenRootCommand = new RelayCommand(() => ClipLibrary.OpenFolder(_store.Current.ClipFolder));
        OpenGameFolderCommand = new RelayCommand(OpenSelectedGameFolder);
        _refreshDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) =>
        {
            _refreshDebounce!.Stop();
            Refresh();
        }, Dispatcher.CurrentDispatcher);
        _store.Changed += config => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (!string.Equals(_watchedRoot, config.ClipFolder, StringComparison.OrdinalIgnoreCase))
            {
                Refresh();
            }
        });
        Refresh();
    }

    /// <summary>The filtered clips split into rows of <see cref="Columns"/> cards.</summary>
    public ObservableCollection<ClipRow> Rows { get; } = [];

    /// <summary>How many cards fit side by side; set by the view when its width changes.</summary>
    public int Columns
    {
        get => _columns;
        set
        {
            if (SetProperty(ref _columns, Math.Max(1, value)))
            {
                BuildRows();
            }
        }
    }

    public ObservableCollection<GameFilter> Filters { get; } = [];

    /// <summary>Newest clips, shown on the home page.</summary>
    public ObservableCollection<ClipItemViewModel> Recent { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand OpenRootCommand { get; }

    public ICommand OpenGameFolderCommand { get; }

    public string RootPath => _store.Current.ClipFolder;

    /// <summary>Show everything, only videos or only screenshots.</summary>
    public LibraryKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                ApplyFilter();
            }
        }
    }

    public GameFilter? SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                ApplyFilter();
                OnPropertyChanged(nameof(CanOpenGameFolder));
            }
        }
    }

    public bool CanOpenGameFolder => SelectedFilter?.Game is not null;

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value ?? ""))
            {
                ApplyFilter();
            }
        }
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetProperty(ref _isEmpty, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>Called when a clip is saved so it shows up immediately.</summary>
    public void ScheduleRefresh()
    {
        _refreshDebounce.Stop();
        _refreshDebounce.Start();
    }

    public void Refresh()
    {
        var config = _store.Current;
        WatchRoot(config.ClipFolder);
        var previousFilter = SelectedFilter?.Game;
        List<ClipInfo> clips;
        try
        {
            ClipLibrary.MoveOldFolders(config);
            clips = ClipLibrary.Scan(config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Não foi possível ler a pasta de clipes: {ex.Message}");
            clips = [];
        }

        var existing = _all.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        _all = clips.Select(c => existing.TryGetValue(c.Path, out var vm) && vm.Info == c ? vm : new ClipItemViewModel(this, c)).ToList();

        Filters.Clear();
        Filters.Add(new GameFilter(null, "Todos", _all.Count));
        foreach (var group in _all.GroupBy(c => c.Game, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            Filters.Add(new GameFilter(group.Key, group.Key, group.Count()));
        }

        _selectedFilter = Filters.FirstOrDefault(f => string.Equals(f.Game, previousFilter, StringComparison.OrdinalIgnoreCase)) ?? Filters[0];
        OnPropertyChanged(nameof(SelectedFilter));
        OnPropertyChanged(nameof(CanOpenGameFolder));
        OnPropertyChanged(nameof(RootPath));
        var totalBytes = _all.Sum(c => c.Info.SizeBytes);
        var videos = _all.Count(c => !c.IsScreenshot);
        var shots = _all.Count - videos;
        Summary = _all.Count == 0
            ? "Nenhum clipe ainda"
            : $"{videos} {(videos == 1 ? "clipe" : "clipes")} · {shots} {(shots == 1 ? "captura" : "capturas")} · {ClipItemViewModel.FormatSize(totalBytes)}";
        Recent.Clear();
        foreach (var clip in _all.Take(4))
        {
            Recent.Add(clip);
        }

        ApplyFilter();
    }

    internal void Remove(ClipItemViewModel clip)
    {
        var answer = MessageBox.Show(
            $"Mover {(clip.IsScreenshot ? "esta captura" : "este clipe")} para a Lixeira?\n\n{clip.FileName}",
            clip.IsScreenshot ? "Excluir captura" : "Excluir clipe",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            ClipLibrary.MoveToRecycleBin(clip.Path);
            Log.Info($"Enviado para a Lixeira: {clip.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Log.Error($"Não foi possível excluir: {ex.Message}");
        }

        Refresh();
    }

    internal AppConfig Config => _store.Current;

    /// <summary>Every clip and screenshot found, newest first.</summary>
    internal IEnumerable<ClipInfo> Library => _all.Select(c => c.Info);

    /// <summary>Opens the clips folder of one game (its folder name, as in <see cref="ClipInfo.Game"/>).</summary>
    internal void OpenGameFolder(string game)
    {
        var folder = Path.Combine(ClipLibrary.CaptureRoot(_store.Current), game);
        ClipLibrary.OpenFolder(Directory.Exists(folder) ? folder : _store.Current.ClipFolder);
    }

    /// <summary>A short message for the corner notice; true when it is a warning.</summary>
    public event Action<string, bool>? Notice;

    /// <summary>Copies the file so the player can paste it in any program (Ctrl+V).</summary>
    internal void Copy(ClipItemViewModel clip)
    {
        if (ClipSharing.CopyToClipboard(clip.Info))
        {
            Notice?.Invoke(clip.IsScreenshot
                ? "Captura copiada. Cole com Ctrl+V onde quiser."
                : "Clipe copiado. Cole com Ctrl+V no Discord, WhatsApp ou outro programa.", false);
        }
        else
        {
            Notice?.Invoke("Não foi possível copiar. Tente de novo.", true);
        }
    }

    /// <summary>
    /// Copies the file and opens the app, so the player only picks the conversation and pastes.
    /// Without the app installed, opens its site instead.
    /// </summary>
    internal void SendTo(ClipItemViewModel clip, ShareApp app)
    {
        if (!ClipSharing.CopyToClipboard(clip.Info))
        {
            Notice?.Invoke("Não foi possível copiar. Tente de novo.", true);
            return;
        }

        var (name, scheme, appLink, site) = app == ShareApp.Discord
            ? ("Discord", "discord", "discord://-/channels/@me", "https://discord.com/app")
            : ("WhatsApp", "whatsapp", "whatsapp://", "https://web.whatsapp.com/");
        var opened = ClipSharing.IsAppInstalled(scheme) ? ClipSharing.Open(appLink) : ClipSharing.Open(site);
        var what = clip.IsScreenshot ? "Captura copiada" : "Clipe copiado";
        if (app == ShareApp.Discord && !clip.IsScreenshot && clip.Info.SizeBytes > ClipSharing.DiscordFreeLimitBytes)
        {
            Notice?.Invoke($"{what}, mas tem {clip.SizeText}: o Discord sem Nitro aceita até 20 MB. " +
                           "Corte no editor ou envie pelo WhatsApp.", true);
        }
        else
        {
            Notice?.Invoke(opened
                ? $"{what}. No {name}, escolha a conversa e cole com Ctrl+V."
                : $"{what}. Abra o {name}, escolha a conversa e cole com Ctrl+V.", false);
        }
    }

    /// <summary>Opens the Windows Share window for the file.</summary>
    internal void Share(ClipItemViewModel clip)
    {
        if (Application.Current?.MainWindow is not { } window || !ClipSharing.ShowShareWindow(window, clip.Info))
        {
            Notice?.Invoke("Não foi possível abrir a janela Compartilhar do Windows.", true);
        }
    }

    /// <summary>Raised after the editor saves a clip, with the saved file and whether it replaced the original.</summary>
    public event Action<string, bool>? EditSaved;

    internal void Edit(ClipItemViewModel clip) =>
        EditorWindow.Open(_store.Current, clip.Path, (path, overwrote) => EditSaved?.Invoke(path, overwrote));

    private void ApplyFilter()
    {
        IEnumerable<ClipItemViewModel> query = _all;
        if (Kind != LibraryKind.All)
        {
            query = query.Where(c => c.IsScreenshot == (Kind == LibraryKind.Screenshots));
        }

        if (SelectedFilter?.Game is { } game)
        {
            query = query.Where(c => c.Game.Equals(game, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            query = query.Where(c => c.FileName.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                                     c.Game.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                                     c.DateText.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        }

        _visible = query.ToList();
        IsEmpty = _visible.Count == 0;
        BuildRows();
    }

    private void BuildRows()
    {
        Rows.Clear();
        for (var i = 0; i < _visible.Count; i += _columns)
        {
            Rows.Add(new ClipRow(_visible.Skip(i).Take(_columns).ToList(), _columns));
        }
    }

    private void OpenSelectedGameFolder()
    {
        if (SelectedFilter?.Game is { } game)
        {
            var folder = Path.Combine(ClipLibrary.CaptureRoot(_store.Current), game);
            ClipLibrary.OpenFolder(Kind == LibraryKind.Screenshots ? Path.Combine(folder, ClipPathBuilder.ScreenshotsSubfolder) : folder);
        }
    }

    private static bool IsLibraryFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".png" or ".jpg";

    private void WatchRoot(string root)
    {
        if (string.Equals(root, _watchedRoot, StringComparison.OrdinalIgnoreCase) && _watcher is not null)
        {
            return;
        }

        _watcher?.Dispose();
        _watcher = null;
        _watchedRoot = root;
        try
        {
            Directory.CreateDirectory(root);
            _watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true };
            FileSystemEventHandler onChange = (_, e) =>
            {
                if (IsLibraryFile(e.FullPath))
                {
                    Application.Current?.Dispatcher.InvokeAsync(ScheduleRefresh);
                }
            };
            _watcher.Created += onChange;
            _watcher.Deleted += onChange;
            _watcher.Renamed += (_, _) => Application.Current?.Dispatcher.InvokeAsync(ScheduleRefresh);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Não foi possível acompanhar a pasta de clipes: {ex.Message}");
        }
    }
}

public sealed class ClipItemViewModel : ObservableObject
{
    private readonly ClipsViewModel _owner;
    private BitmapSource? _thumbnail;
    private string _durationText = "";
    private bool _loadStarted;

    public ClipItemViewModel(ClipsViewModel owner, ClipInfo info)
    {
        _owner = owner;
        Info = info;
        PlayCommand = new RelayCommand(() => ClipLibrary.Play(Path));
        RevealCommand = new RelayCommand(() => ClipLibrary.RevealInExplorer(Path));
        DeleteCommand = new RelayCommand(() => _owner.Remove(this));
        EditCommand = new RelayCommand(() => _owner.Edit(this), () => !IsScreenshot);
        CopyCommand = new RelayCommand(() => _owner.Copy(this));
        SendToDiscordCommand = new RelayCommand(() => _owner.SendTo(this, ShareApp.Discord));
        SendToWhatsAppCommand = new RelayCommand(() => _owner.SendTo(this, ShareApp.WhatsApp));
        ShareCommand = new RelayCommand(() => _owner.Share(this));
    }

    public ClipInfo Info { get; }

    public string Path => Info.Path;

    public string Game => Info.Game;

    public bool IsScreenshot => Info.IsScreenshot;

    public string KindText => IsScreenshot ? "Captura" : "Clipe";

    public string OpenText => IsScreenshot ? "Abrir" : "Assistir";

    public string OpenIcon => IsScreenshot ? "" : "";

    public string FileName => System.IO.Path.GetFileName(Info.Path);

    public string DateText => Info.Created.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);

    public string SizeText => FormatSize(Info.SizeBytes);

    public ICommand PlayCommand { get; }

    public ICommand RevealCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand CopyCommand { get; }

    public ICommand SendToDiscordCommand { get; }

    public ICommand SendToWhatsAppCommand { get; }

    public ICommand ShareCommand { get; }

    /// <summary>Loaded lazily the first time the card is shown.</summary>
    public BitmapSource? Thumbnail
    {
        get
        {
            EnsureLoaded();
            return _thumbnail;
        }
    }

    public string DurationText
    {
        get
        {
            EnsureLoaded();
            return _durationText;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    private void EnsureLoaded()
    {
        if (_loadStarted)
        {
            return;
        }

        _loadStarted = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var config = _owner.Config;
        var info = Info;

        // Everything slow (ffprobe, ffmpeg, decoding the image) runs off the UI thread, a few clips at a time,
        // so opening the Clips page never freezes the window.
        var (seconds, image) = await Task.Run(async () =>
        {
            var duration = await ClipLibrary.GetDurationAsync(config, info);
            var thumbnailPath = await ClipLibrary.GetThumbnailAsync(config, info);
            if (thumbnailPath is null)
            {
                return (duration, (BitmapSource?)null);
            }

            await DecodeSlots.WaitAsync();
            try
            {
                return (duration, LoadThumbnail(info, thumbnailPath));
            }
            finally
            {
                DecodeSlots.Release();
            }
        });

        if (seconds is { } value)
        {
            var span = TimeSpan.FromSeconds(Math.Round(value));
            _durationText = span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
            OnPropertyChanged(nameof(DurationText));
        }

        if (image is not null)
        {
            _thumbnail = image;
            OnPropertyChanged(nameof(Thumbnail));
        }
    }

    private static readonly SemaphoreSlim DecodeSlots = new(2, 2);

    /// <summary>
    /// Decodes the thumbnail at 480 px. A full-size screenshot (several MB) is decoded only once: a small JPEG copy
    /// is saved in the thumbnail cache and used from then on.
    /// </summary>
    private static BitmapSource? LoadThumbnail(ClipInfo info, string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = 480;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();

            if (info.IsScreenshot && string.Equals(path, info.Path, StringComparison.OrdinalIgnoreCase))
            {
                SaveScreenshotThumbnail(image, ClipLibrary.ThumbnailCachePath(info));
            }

            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null; // Leave the placeholder.
        }
    }

    private static void SaveScreenshotThumbnail(BitmapSource image, string target)
    {
        var temporary = target + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(temporary))
            {
                encoder.Save(stream);
            }

            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            // Not cached; decoded from the screenshot again next time.
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
