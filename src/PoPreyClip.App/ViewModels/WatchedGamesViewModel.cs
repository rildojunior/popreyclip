using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using PoPreyClip.Clips;
using PoPreyClip.Config;
using PoPreyClip.Games;

namespace PoPreyClip.App.ViewModels;

/// <summary>An open window the user can pick to watch.</summary>
public sealed record OpenWindow(string ExeName, string Title)
{
    public string Display => $"{Title}  ({ExeName})";

    public override string ToString() => Display;
}

/// <summary>One entry of the games / watched apps list (executable → name used for the clip folder).</summary>
public sealed class WatchedGameItem(WatchedGamesViewModel owner, string exeName, string name)
{
    public string ExeName { get; } = exeName;

    public string Name { get; } = name;

    public ICommand RemoveCommand { get; } = new RelayCommand(() => owner.Remove(exeName));
}

/// <summary>
/// "Jogos e apps vigiados": programs that count as a game as soon as they open, even when they are not
/// fullscreen or in the foreground. Each one starts recording in idle mode and gets its own clip folder.
/// </summary>
public sealed class WatchedGamesViewModel : ObservableObject
{
    private readonly ConfigStore _store;
    private OpenWindow? _selectedWindow;
    private string _newName = "";
    private bool _loading;

    public WatchedGamesViewModel(ConfigStore store)
    {
        _store = store;
        AddCommand = new RelayCommand(Add, () => SelectedWindow is not null && ClipPathBuilder.SanitizeFolderName(NewName) != ClipPathBuilder.UnknownGame);
        RefreshWindowsCommand = new AsyncRelayCommand(LoadWindowsAsync);
        LoadItems();
        _ = LoadWindowsAsync();
    }

    public ObservableCollection<WatchedGameItem> Items { get; } = [];

    public ObservableCollection<OpenWindow> OpenWindows { get; } = [];

    public ICommand AddCommand { get; }

    public ICommand RefreshWindowsCommand { get; }

    public OpenWindow? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (SetProperty(ref _selectedWindow, value) && value is not null)
            {
                // Suggest the window title as the game name; the user can edit it.
                NewName = GameDetector.CleanName(value.Title.Length <= 40 ? value.Title : Path.GetFileNameWithoutExtension(value.ExeName));
            }
        }
    }

    /// <summary>Name shown in the app and used as the clip folder name.</summary>
    public string NewName
    {
        get => _newName;
        set => SetProperty(ref _newName, value ?? "");
    }

    public bool Loading
    {
        get => _loading;
        private set => SetProperty(ref _loading, value);
    }

    public bool IsEmpty => Items.Count == 0;

    internal void Remove(string exeName)
    {
        _store.Update(c => c.Games.Remove(exeName));
        LoadItems();
        Log.Info($"{exeName} removido dos jogos vigiados.");
    }

    private void Add()
    {
        if (SelectedWindow is not { } window)
        {
            return;
        }

        var name = GameDetector.CleanName(NewName);
        _store.Update(c => c.Games[window.ExeName] = name);
        LoadItems();
        Log.Info($"{window.ExeName} agora é vigiado como \"{name}\". A captura de clipes liga quando ele abrir.");
        SelectedWindow = null;
        NewName = "";
    }

    private void LoadItems()
    {
        Items.Clear();
        foreach (var (exe, name) in _store.Current.Games.OrderBy(g => g.Value, StringComparer.CurrentCultureIgnoreCase))
        {
            Items.Add(new WatchedGameItem(this, exe, name));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task LoadWindowsAsync()
    {
        Loading = true;
        var windows = await Task.Run(FindOpenWindows);
        OpenWindows.Clear();
        foreach (var window in windows)
        {
            OpenWindows.Add(window);
        }

        Loading = false;
    }

    /// <summary>Programs with a visible titled window, one per executable, excluding Windows itself and this app.</summary>
    private static List<OpenWindow> FindOpenWindows()
    {
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var result = new Dictionary<string, OpenWindow>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero ||
                        string.IsNullOrWhiteSpace(process.MainWindowTitle))
                    {
                        continue;
                    }

                    var path = PoPreyClip.Native.Win32.GetProcessPath(process.Id);
                    if (path is null || path.StartsWith(windowsFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var exe = Path.GetFileName(path);
                    result.TryAdd(exe, new OpenWindow(exe, process.MainWindowTitle.Trim()));
                }
                catch (InvalidOperationException)
                {
                    // Process exited while listing.
                }
            }
        }

        return result.Values.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
