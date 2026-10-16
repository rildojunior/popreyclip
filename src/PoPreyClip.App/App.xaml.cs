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
    // POPREYCLIP_INSTANCE lets automated tests run a separate copy next to the one the user has open.
    private static readonly string InstanceSuffix = Environment.GetEnvironmentVariable("POPREYCLIP_INSTANCE") ?? "";
    private static readonly string InstanceMutexName = "PoPreyClip.SingleInstance" + InstanceSuffix;
    private static readonly string ShowEventName = "PoPreyClip.ShowWindow" + InstanceSuffix;

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
        // Errors outside the UI thread would otherwise close the app without a trace in the log.
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error($"Erro fatal: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error($"Erro em tarefa de fundo: {args.Exception}");
            args.SetObserved();
        };

        var args = e.Args;
        ForwardArguments = ForwardedFrom(args);
        if (GetArgument(args, "--install-update") is { } updateTarget)
        {
            // This process is the new version running from the downloads folder: replace the app and reopen it.
            int.TryParse(GetArgument(args, "--wait-pid"), out var installerWaitPid);
            var error = PoPreyClip.Updates.UpdateService.RunInstaller(updateTarget, installerWaitPid, ForwardArguments);
            if (error is not null)
            {
                Log.Error(error);
                MessageBox.Show($"Não foi possível instalar a atualização. O PoPreyClip vai abrir na versão anterior.\n\n{error}",
                    "PoPreyClip", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Shutdown();
            return;
        }

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
        var justUpdated = args.Contains("--updated");

        // After a self-update the new process waits for the old one to exit before taking the single-instance lock.
        if (GetArgument(args, "--wait-pid") is { } waitPid && int.TryParse(waitPid, out var oldPid))
        {
            WaitForExit(oldPid);
        }

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
        _viewModel = new MainViewModel(_store, _session, _hotkeys, () => Dispatcher.InvokeAsync(ExitApplication));

        var window = new MainWindow(_viewModel, _store);
        MainWindow = window;

        if (snapshotDirectory is not null)
        {
            _ = RunSnapshotAsync(window, snapshotDirectory, args.Contains("--record"));
            return;
        }

        _tray = new TrayIcon(window, _viewModel, ExitApplication);
        window.ExitRequested += ExitApplication;
        // --minimized (start with Windows) starts in the tray; the tray icon opens the window.
        if (!startHidden)
        {
            window.Show();
        }

        try
        {
            new PoPreyClip.Startup.WindowsStartup().RefreshPath();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"Could not refresh start with Windows: {ex.Message}");
        }

        _viewModel.StartOnLaunch();
        _viewModel.Updates.Start();
        _ = PoPreyClip.Updates.UpdateService.CleanupAfterUpdateAsync();
        if (justUpdated)
        {
            _viewModel.ShowNotice($"PoPreyClip atualizado para a versão {PoPreyClip.Updates.UpdateService.CurrentVersion.ToString(3)}");
        }
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
        window.Height = int.TryParse(GetArgument(Environment.GetCommandLineArgs(), "--snapshot-height"), out var snapshotHeight) ? snapshotHeight : 1450;
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

            // Long pages also get a picture of their end (the window can't be taller than the screen).
            if (FindScrollViewer(window) is { ScrollableHeight: > 0 } scroller)
            {
                scroller.ScrollToEnd();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveSnapshot(window, Path.Combine(directory, $"{(int)page}-{page}-fim.png"));
                scroller.ScrollToHome();
            }
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

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.ScrollViewer { ScrollableHeight: > 0 } scroller)
            {
                return scroller;
            }

            if (FindScrollViewer(child) is { } found)
            {
                return found;
            }
        }

        return null;
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

    private static void WaitForExit(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            process.WaitForExit(30_000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    /// <summary>Options that must survive a restart after an update (--config and --minimized).</summary>
    internal static IReadOnlyList<string> ForwardArguments { get; private set; } = [];

    private static List<string> ForwardedFrom(string[] args)
    {
        var forwarded = new List<string>();
        if (GetArgument(args, "--config") is { } config)
        {
            forwarded.AddRange(["--config", config]);
        }

        if (args.Contains("--minimized"))
        {
            forwarded.Add("--minimized");
        }

        return forwarded;
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
