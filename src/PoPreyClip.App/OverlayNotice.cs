using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using PoPreyClip.Native;

namespace PoPreyClip.App;

/// <summary>
/// PoPreyClip's own notice ("clip saved", "ready to clip"), drawn in the top-right corner of the monitor the player is using.
/// Unlike Windows notifications it still shows while Windows' do-not-disturb hides them during games.
/// It is a plain window on top (nothing is injected into the game), never takes focus, lets clicks through
/// and is closed, not hidden, after a few seconds, so it uses no memory or CPU while it is off screen.
/// </summary>
internal sealed class OverlayNotice : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpNoActivate = 0x10;
    private const double MarginDip = 16;
    // Transparent room around the notice so its shadow is not cut off by the window edge.
    private const double ShadowDip = 14;
    public const string CheckGlyph = "";
    public const string GameGlyph = "";
    private const int BarFramesPerSecond = 20;
    private const double CardRadius = 12;
    private const double CardBorder = 2;
    private const double InnerRadius = CardRadius - CardBorder;
    private static readonly TimeSpan DefaultVisibleTime = TimeSpan.FromSeconds(3);
    private static readonly Duration FadeTime = TimeSpan.FromMilliseconds(150);
    private static readonly Brush CardBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x21)));
    private static readonly Brush DetailBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC9, 0xCD, 0xE0)));

    private static OverlayNotice? _current;

    private readonly IntPtr _targetWindow;
    private readonly DispatcherTimer _timer;
    private readonly ScaleTransform _timeLeft = new(1, 1);

    private OverlayNotice(string title, string detail, string glyph, TimeSpan visibleTime, IntPtr targetWindow)
    {
        _targetWindow = targetWindow;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Off screen until it is placed on the player's monitor.
        Left = -32000;
        Top = -32000;
        Opacity = 0;
        Content = BuildContent(title, detail, glyph, _timeLeft);

        _timer = new DispatcherTimer { Interval = visibleTime };
        _timer.Tick += (_, _) => FadeOutAndClose();
        SourceInitialized += (_, _) => MakeNonInteractive();
        Loaded += (_, _) =>
        {
            PlaceOnTargetMonitor();
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeTime));
            // The green bar empties while the notice is on screen. Each frame redraws the whole see-through window
            // in software, so the bar moves at 20 frames per second instead of 60: it still looks smooth and
            // costs about half the CPU.
            var drain = new DoubleAnimation(1, 0, visibleTime);
            Timeline.SetDesiredFrameRate(drain, BarFramesPerSecond);
            _timeLeft.BeginAnimation(ScaleTransform.ScaleXProperty, drain);
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (_current == this)
            {
                _current = null;
            }
        };
    }

    /// <summary>
    /// Shows a notice on the monitor of the current foreground window, replacing the previous one. Each line of
    /// <paramref name="detail"/> is shown on its own row. UI thread only.
    /// </summary>
    public static void Notify(string title, string detail, string glyph = CheckGlyph, TimeSpan? visibleTime = null)
    {
        _current?.Close();
        _current = new OverlayNotice(title, detail, glyph, visibleTime ?? DefaultVisibleTime, Win32.GetForegroundWindow());
        _current.Show();
    }

    private static UIElement BuildContent(string title, string detail, string glyph, ScaleTransform timeLeft)
    {
        var app = Application.Current;
        var success = (Brush)app.FindResource("SuccessBrush");
        var icon = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(21),
            Background = success,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)app.FindResource("IconFont"),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
        });
        text.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 13.5,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = DetailBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var row = new DockPanel { Margin = new Thickness(14, 12, 20, 12) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);

        var bar = new Border
        {
            Height = 4,
            Background = success,
            RenderTransform = timeLeft,
            RenderTransformOrigin = new Point(0, 0.5),
        };

        var layout = new StackPanel();
        layout.Children.Add(row);
        layout.Children.Add(bar);
        // A 4 px bar cannot have a 10 px rounded corner, so the content is clipped to the inside of the card's
        // rounded border instead (12 px outer radius minus the 2 px border).
        layout.SizeChanged += (_, e) => layout.Clip = new RectangleGeometry(new Rect(e.NewSize), InnerRadius, InnerRadius);

        return new Border
        {
            Margin = new Thickness(ShadowDip),
            MinWidth = 290,
            MaxWidth = 380,
            CornerRadius = new CornerRadius(CardRadius),
            Background = CardBrush,
            BorderBrush = success,
            BorderThickness = new Thickness(CardBorder),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 6, Direction = 270, Opacity = 0.55, Color = Colors.Black },
            Child = layout,
        };
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>No focus stealing (a fullscreen game could minimize), no taskbar or Alt+Tab entry, clicks pass through.</summary>
    private void MakeNonInteractive()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongW(hwnd, GwlExStyle);
        SetWindowLongW(hwnd, GwlExStyle, style | WsExNoActivate | WsExToolWindow | WsExTransparent);
    }

    /// <summary>Works in screen pixels, so it lands in the right corner whatever the scale of each monitor.</summary>
    private void PlaceOnTargetMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = Win32.GetMonitorBounds(_targetWindow);
        // Moving onto the monitor first lets Windows rescale the window for that monitor before measuring it.
        SetWindowPos(hwnd, IntPtr.Zero, monitor.Left, monitor.Top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        if (!Win32.GetWindowRect(hwnd, out var size))
        {
            return;
        }

        // The window includes the shadow room, so the visible card ends up MarginDip away from the edges.
        var margin = (int)Math.Round((MarginDip - ShadowDip) * GetDpiForWindow(hwnd) / 96.0);
        var width = size.Right - size.Left;
        SetWindowPos(hwnd, IntPtr.Zero, monitor.Right - width - margin, monitor.Top + margin, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private void FadeOutAndClose()
    {
        _timer.Stop();
        var fade = new DoubleAnimation(0, FadeTime);
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
