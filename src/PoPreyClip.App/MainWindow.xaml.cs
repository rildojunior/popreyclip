using System.ComponentModel;
using System.Windows;
using PoPreyClip.App.ViewModels;
using PoPreyClip.Config;

namespace PoPreyClip.App;

public partial class MainWindow : Window
{
    private readonly ConfigStore _store;

    public MainWindow(MainViewModel viewModel, ConfigStore store)
    {
        InitializeComponent();
        DataContext = viewModel;
        _store = store;
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    /// <summary>Set before closing for real (exit from the tray menu).</summary>
    public bool AllowClose { get; set; }

    public event Action? ExitRequested;

    public event Action? HiddenToTray;

    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && _store.Current.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            return;
        }

        base.OnClosing(e);
        if (!AllowClose)
        {
            // Closing without the tray option exits the app.
            AllowClose = true;
            Dispatcher.InvokeAsync(() => ExitRequested?.Invoke());
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
