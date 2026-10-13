using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PoPreyClip.App.ViewModels;
using PoPreyClip.Config;
using PoPreyClip.Hotkeys;
using PoPreyClip.Session;

namespace PoPreyClip.App;

public partial class App : Application
{
    private const string InstanceMutexName = "PoPreyClip.SingleInstance";
    private const string ShowEventName = "PoPreyClip.ShowWindow";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private ConfigStore? _store;
    private RecordingSession? _session;
    private HotkeyManager? _hotkeys;
    private MainViewModel? _viewModel;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.WriteToConsole = false;
        DispatcherUnhandledException += OnUnhandledException;

        var args = e.Args;
        if (GetArgument(args, "--export-sounds") is { } soundDirectory)
        {
            // Test helper: writes every built-in notification sound as a WAV file.
            Directory.CreateDirectory(soundDirectory);
            foreach (var id in ClipSoundPlayer.BuiltIn.Select(s => s.Id).Append(ClipSoundPlayer.ShutterId))
            {
                File.WriteAllBytes(Path.Combine(soundDirectory, id + ".wav"), ClipSoundPlayer.ToWav(ClipSoundPlayer.Synthesize(id)));
            }

            Shutdown();
            return;
        }

        var snapshotDirectory = GetArgument(args, "--snapshot");
        var configPath = GetArgument(args, "--config");
        var startHidden = args.Contains("--minimized");

        if (snapshotDirectory is null && !AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        try
        {
            _store = ConfigStore.LoadOrCreate(configPath);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show($"Não foi possível ler a configuração:\n\n{ex.Message}", "PoPreyClip", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _store.StartWatching();
        _session = new RecordingSession(_store);
        _hotkeys = new HotkeyManager();
        _viewModel = new MainViewModel(_store, _session, _hotkeys);

        var window = new MainWindow(_viewModel, _store);
        MainWindow = window;

        if (snapshotDirectory is not null)
        {
            _ = RunSnapshotAsync(window, snapshotDirectory, args.Contains("--record"));
            return;
        }

        _tray = new TrayIcon(window, _viewModel, ExitApplication);
        window.ExitRequested += ExitApplication;
        if (!(startHidden && _store.Current.MinimizeToTray))
        {
            window.Show();
        }

        _viewModel.StartOnLaunch();
    }

    private void ExitApplication()
    {
        if (MainWindow is MainWindow window)
        {
            window.AllowClose = true;
            window.Close();
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Each step runs even if an earlier one fails, so recording always stops and settings are saved.
        // Stopping the session happens off the UI thread, where the audio objects were created.
        SafeExitStep("bandeja", () => _tray?.Dispose());
        SafeExitStep("interface", () => _viewModel?.Dispose());
        SafeExitStep("gravação", () => Task.Run(() => _session?.Dispose()).GetAwaiter().GetResult());
        SafeExitStep("atalhos", () => _hotkeys?.Dispose());
        SafeExitStep("configuração", () => _store?.Dispose());
        SafeExitStep("instância", () =>
        {
            _showEvent?.Dispose();
            if (_instanceMutex is not null)
            {
                _instanceMutex.ReleaseMutex();
                _instanceMutex.Dispose();
            }
        });

        base.OnExit(e);
    }

    private static void SafeExitStep(string name, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Log.Error($"Erro ao encerrar ({name}): {ex}");
        }
    }

    /// <summary>Only one PoPreyClip records at a time; a second launch brings the first window forward.</summary>
    private bool AcquireSingleInstance()
    {
        _instanceMutex = new Mutex(true, InstanceMutexName, out var createdNew);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!createdNew)
        {
            _showEvent.Set();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }

        var showEvent = _showEvent;
        var listener = new Thread(() =>
        {
            try
            {
                while (showEvent.WaitOne())
                {
                    Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.BringToFront());
                }
            }
            catch (ObjectDisposedException)
            {
            }
        })
        {
            IsBackground = true,
            Name = "PoPreyClip single instance",
        };
        listener.Start();
        return true;
    }

    /// <summary>Test helper: renders every page to PNG files and exits.</summary>
    private async Task RunSnapshotAsync(MainWindow window, string directory, bool record)
    {
        Directory.CreateDirectory(directory);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000;
        window.Top = 0;
        window.ShowActivated = false;
        window.Height = 1450;
        window.Show();
        if (record)
        {
            await _viewModel!.ToggleRecordingAsync();
            await Task.Delay(TimeSpan.FromSeconds(4));
        }

        foreach (var page in Enum.GetValues<AppPage>())
        {
            _viewModel!.Page = page;
            await Task.Delay(page == AppPage.Clips ? 3500 : 1200);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveSnapshot(window, Path.Combine(directory, $"{(int)page}-{page}.png"));
        }

        if (record)
        {
            await _viewModel!.SaveClipAsync();
            await Task.Delay(2500);
            _viewModel.Page = AppPage.Home;
            await Task.Delay(1500);
            SaveSnapshot(window, Path.Combine(directory, "4-AfterClip.png"));
            // Recording is left running on purpose: exiting must stop it cleanly, like Sair in the tray menu.
        }

        window.AllowClose = true;
        window.Close();
        Shutdown();
    }

    private static void SaveSnapshot(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap(
            (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string? GetArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error($"Erro inesperado: {e.Exception}");
        MessageBox.Show($"Ocorreu um erro inesperado:\n\n{e.Exception.Message}", "PoPreyClip", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
