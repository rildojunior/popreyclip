using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PoPreyClip.Config;
using PoPreyClip.Library;

namespace PoPreyClip.App.ViewModels;

public enum LibraryKind
{
    All,
    Videos,
    Screenshots,
}

public sealed record GameFilter(string? Game, string Label, int Count)
{
    public string Display => $"{Label}  {Count}";
}

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

    public ObservableCollection<ClipItemViewModel> Clips { get; } = [];

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

        Clips.Clear();
        foreach (var clip in query)
        {
            Clips.Add(clip);
        }

        IsEmpty = Clips.Count == 0;
    }

    private void OpenSelectedGameFolder()
    {
        if (SelectedFilter?.Game is { } game)
        {
            var root = Kind == LibraryKind.Screenshots ? ClipLibrary.ScreenshotsRoot(_store.Current) : ClipLibrary.ClipsRoot(_store.Current);
            ClipLibrary.OpenFolder(Path.Combine(root, game));
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
    private BitmapImage? _thumbnail;
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

    /// <summary>Loaded lazily the first time the card is shown.</summary>
    public BitmapImage? Thumbnail
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
        var duration = await ClipLibrary.GetDurationAsync(config, Info);
        if (duration is { } seconds)
        {
            var span = TimeSpan.FromSeconds(Math.Round(seconds));
            _durationText = span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
            OnPropertyChanged(nameof(DurationText));
        }

        var thumbnailPath = await ClipLibrary.GetThumbnailAsync(config, Info);
        if (thumbnailPath is null)
        {
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 480;
            image.UriSource = new Uri(thumbnailPath);
            image.EndInit();
            image.Freeze();
            _thumbnail = image;
            OnPropertyChanged(nameof(Thumbnail));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            // Leave the placeholder.
        }
    }
}
