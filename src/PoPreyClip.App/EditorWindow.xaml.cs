using System.Runtime;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PoPreyClip.App.ViewModels;
using PoPreyClip.Config;

namespace PoPreyClip.App;

/// <summary>
/// Clip editor. Created only when the user opens it and destroyed (not hidden) when closed, so it costs nothing the
/// rest of the time. Video frames come from <see cref="Playback.ClipPlayer"/> and are drawn into one bitmap; the render
/// loop only runs while playing or while waiting for a frame.
/// </summary>
public partial class EditorWindow : Window
{
    private static EditorWindow? _current;

    private readonly EditorViewModel _viewModel;
    private WriteableBitmap? _bitmap;
    private bool _ticking;
    private bool _resumeAfterSeek;
    private DateTime _awaitFrameUntil;

    private EditorWindow(AppConfig config, string path)
    {
        InitializeComponent();
        _viewModel = new EditorViewModel(config, path);
        DataContext = _viewModel;
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        Timeline.Seek += OnSeek;
        Loaded += async (_, _) => await LoadAsync();
    }

    public string ClipPath => _viewModel.Path;

    /// <summary>Opens the editor for a clip, or brings it forward if it is already open.</summary>
    public static void Open(AppConfig config, string path, Action<string, bool> saved)
    {
        if (_current is { } open)
        {
            if (open._viewModel.IsSaving || string.Equals(open.ClipPath, path, StringComparison.OrdinalIgnoreCase))
            {
                open.Activate();
                return;
            }

            open.Close();
        }

        var window = new EditorWindow(config, path);
        window._viewModel.Saved += (savedPath, overwrote) =>
        {
            saved(savedPath, overwrote);
            window.Close();
        };
        _current = window;
        window.Show();
    }

    private async Task LoadAsync()
    {
        // Decoding at the size the video is shown keeps the preview cheap; bigger frames would only be scaled down again.
        var dpi = VisualTreeHelper.GetDpi(this);
        await _viewModel.LoadAsync((int)(VideoHost.ActualWidth * dpi.DpiScaleX), (int)(VideoHost.ActualHeight * dpi.DpiScaleY));
        if (_viewModel.Player is not { } player)
        {
            return;
        }

        _bitmap = new WriteableBitmap(player.FrameWidth, player.FrameHeight, 96, 96, PixelFormats.Bgr32, null);
        VideoImage.Source = _bitmap;
        ShowFrame(0);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Space && !_viewModel.IsSaving && _viewModel.Player is not null)
        {
            TogglePlayback();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        StopTicking();
        _viewModel.Dispose();
        VideoImage.Source = null;
        _bitmap = null;
        if (_current == this)
        {
            _current = null;
        }

        // The frame bitmap and the window's graphics resources are native memory freed by finalizers, which could take
        // minutes to run on their own. A full collection right after closing gives everything back at once (a few ms).
        Dispatcher.InvokeAsync(() =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (_viewModel.Player is not { } player)
        {
            return;
        }

        if (player.IsPlaying)
        {
            Pause();
            return;
        }

        var from = _viewModel.Position;
        if (from < _viewModel.Start - 0.01 || from >= _viewModel.End - 0.05)
        {
            from = _viewModel.Start;
        }

        player.Play(from);
        PlayIcon.Text = "";
        StartTicking();
    }

    private void Pause()
    {
        if (_viewModel.Player is not { } player)
        {
            return;
        }

        player.Pause();
        _viewModel.Position = player.Position;
        PlayIcon.Text = "";
    }

    private void OnSeek(double seconds, bool final)
    {
        if (_viewModel.Player is not { } player)
        {
            return;
        }

        if (player.IsPlaying)
        {
            _resumeAfterSeek = true;
            Pause();
        }

        if (final && _resumeAfterSeek)
        {
            _resumeAfterSeek = false;
            _viewModel.Position = seconds;
            player.Play(seconds);
            PlayIcon.Text = "";
            StartTicking();
            return;
        }

        ShowFrame(seconds);
    }

    private void ShowFrame(double seconds)
    {
        _viewModel.Position = seconds;
        _viewModel.Player?.ShowFrameAt(seconds);
        _awaitFrameUntil = DateTime.UtcNow.AddSeconds(3);
        StartTicking();
    }

    private void StartTicking()
    {
        if (!_ticking)
        {
            _ticking = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void StopTicking()
    {
        if (_ticking)
        {
            _ticking = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_viewModel.Player is not { } player || _bitmap is not { } bitmap)
        {
            StopTicking();
            return;
        }

        player.PresentFrame(data =>
            bitmap.WritePixels(new Int32Rect(0, 0, player.FrameWidth, player.FrameHeight), data, player.FrameWidth * 4, 0));
        if (player.IsPlaying)
        {
            var position = player.Position;
            if (position >= _viewModel.End || player.ReachedEnd)
            {
                Pause();
                _viewModel.Position = Math.Min(position, _viewModel.End);
            }
            else
            {
                _viewModel.Position = position;
            }

            return;
        }

        if (!player.HasPendingFrame || DateTime.UtcNow > _awaitFrameUntil)
        {
            StopTicking();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
