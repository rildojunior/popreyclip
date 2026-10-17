using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PoPreyClip.App.Controls;

/// <summary>
/// Clip timeline for the editor: a strip of thumbnails, two handles that pick the part to keep and a playhead.
/// Drawn in <see cref="OnRender"/> so it costs nothing while nothing changes.
/// </summary>
public sealed class TrimTimeline : FrameworkElement
{
    public static readonly DependencyProperty DurationProperty = Register(nameof(Duration), 0.0);
    public static readonly DependencyProperty StartProperty = Register(nameof(Start), 0.0, twoWay: true);
    public static readonly DependencyProperty EndProperty = Register(nameof(End), 0.0, twoWay: true);
    public static readonly DependencyProperty PositionProperty = Register(nameof(Position), 0.0);
    public static readonly DependencyProperty RealStartProperty = Register(nameof(RealStart), 0.0);

    public static readonly DependencyProperty ThumbnailsProperty = DependencyProperty.Register(
        nameof(Thumbnails), typeof(ImageSource), typeof(TrimTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Shortest part the handles can select, in seconds.</summary>
    private const double MinimumLength = 0.5;
    private const double HandleWidth = 14;
    private const double StripMargin = 6;

    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x30)));
    private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB4, 0x0B, 0x0D, 0x12)));
    private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1)));
    private static readonly Brush RealStartBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0xF5, 0x9E, 0x0B)));
    private static readonly Brush GripBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen AccentPen = Frozen(new Pen(AccentBrush, 3));
    private static readonly Pen PlayheadPen = Frozen(new Pen(Brushes.White, 2));
    private static readonly Pen GripPen = Frozen(new Pen(GripBrush, 1.5));

    private enum DragPart
    {
        None,
        Start,
        End,
        Playhead,
    }

    private DragPart _drag;
    private double _grabOffset;

    public TrimTimeline()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    /// <summary>Raised while the user moves the playhead or a handle (final = false) and when they let go (final = true).</summary>
    public event Action<double, bool>? Seek;

    public double Duration
    {
        get => (double)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public double Start
    {
        get => (double)GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    public double End
    {
        get => (double)GetValue(EndProperty);
        set => SetValue(EndProperty, value);
    }

    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    /// <summary>Where the saved clip will really start (a keyframe before <see cref="Start"/> when the video is copied).</summary>
    public double RealStart
    {
        get => (double)GetValue(RealStartProperty);
        set => SetValue(RealStartProperty, value);
    }

    public ImageSource? Thumbnails
    {
        get => (ImageSource?)GetValue(ThumbnailsProperty);
        set => SetValue(ThumbnailsProperty, value);
    }

    private Rect Strip => new(HandleWidth, StripMargin, Math.Max(1, ActualWidth - 2 * HandleWidth), Math.Max(1, ActualHeight - 2 * StripMargin));

    private double ToX(double seconds) => Strip.Left + (Duration > 0 ? Math.Clamp(seconds / Duration, 0, 1) : 0) * Strip.Width;

    private double ToSeconds(double x) => Duration <= 0 ? 0 : Math.Clamp((x - Strip.Left) / Strip.Width, 0, 1) * Duration;

    protected override void OnRender(DrawingContext dc)
    {
        var strip = Strip;
        dc.PushClip(new RectangleGeometry(strip, 6, 6));
        dc.DrawRectangle(TrackBrush, null, strip);
        if (Thumbnails is { } thumbnails)
        {
            dc.DrawImage(thumbnails, strip);
        }

        var startX = ToX(Start);
        var endX = ToX(End);
        dc.DrawRectangle(DimBrush, null, new Rect(strip.Left, strip.Top, Math.Max(0, startX - strip.Left), strip.Height));
        dc.DrawRectangle(DimBrush, null, new Rect(endX, strip.Top, Math.Max(0, strip.Right - endX), strip.Height));
        var realX = ToX(RealStart);
        if (startX - realX > 1)
        {
            dc.DrawRectangle(RealStartBrush, null, new Rect(realX, strip.Top, startX - realX, strip.Height));
        }

        dc.Pop();

        // Selection frame and its two handles.
        dc.DrawLine(AccentPen, new Point(startX, strip.Top + 1.5), new Point(endX, strip.Top + 1.5));
        dc.DrawLine(AccentPen, new Point(startX, strip.Bottom - 1.5), new Point(endX, strip.Bottom - 1.5));
        DrawHandle(dc, new Rect(startX - HandleWidth, strip.Top, HandleWidth, strip.Height), new CornerRadius(6, 0, 0, 6));
        DrawHandle(dc, new Rect(endX, strip.Top, HandleWidth, strip.Height), new CornerRadius(0, 6, 6, 0));

        var playX = ToX(Position);
        dc.DrawLine(PlayheadPen, new Point(playX, 0), new Point(playX, ActualHeight));
        dc.DrawEllipse(Brushes.White, null, new Point(playX, 3), 4, 4);
    }

    private static void DrawHandle(DrawingContext dc, Rect rect, CornerRadius corners)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(rect.Left + corners.TopLeft, rect.Top), true, true);
            g.LineTo(new Point(rect.Right - corners.TopRight, rect.Top), false, false);
            g.ArcTo(new Point(rect.Right, rect.Top + corners.TopRight), new Size(corners.TopRight, corners.TopRight), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(rect.Right, rect.Bottom - corners.BottomRight), false, false);
            g.ArcTo(new Point(rect.Right - corners.BottomRight, rect.Bottom), new Size(corners.BottomRight, corners.BottomRight), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(rect.Left + corners.BottomLeft, rect.Bottom), false, false);
            g.ArcTo(new Point(rect.Left, rect.Bottom - corners.BottomLeft), new Size(corners.BottomLeft, corners.BottomLeft), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(rect.Left, rect.Top + corners.TopLeft), false, false);
            g.ArcTo(new Point(rect.Left + corners.TopLeft, rect.Top), new Size(corners.TopLeft, corners.TopLeft), 0, false, SweepDirection.Clockwise, false, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(AccentBrush, null, geometry);
        var middleX = rect.Left + rect.Width / 2;
        var middleY = rect.Top + rect.Height / 2;
        dc.DrawLine(GripPen, new Point(middleX - 2, middleY - 7), new Point(middleX - 2, middleY + 7));
        dc.DrawLine(GripPen, new Point(middleX + 2, middleY - 7), new Point(middleX + 2, middleY + 7));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Duration <= 0)
        {
            return;
        }

        var x = e.GetPosition(this).X;
        var startX = ToX(Start);
        var endX = ToX(End);
        _drag = x >= startX - HandleWidth - 2 && x <= startX + 3 ? DragPart.Start
            : x >= endX - 3 && x <= endX + HandleWidth + 2 ? DragPart.End
            : DragPart.Playhead;
        // Grabbing a handle off-center must not make it jump.
        _grabOffset = _drag == DragPart.Start ? startX - x : _drag == DragPart.End ? endX - x : 0;
        CaptureMouse();
        MoveTo(x, final: false);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag != DragPart.None && e.LeftButton == MouseButtonState.Pressed)
        {
            MoveTo(e.GetPosition(this).X, final: false);
        }
        else if (_drag == DragPart.None)
        {
            var x = e.GetPosition(this).X;
            var startX = ToX(Start);
            var endX = ToX(End);
            var onHandle = (x >= startX - HandleWidth - 2 && x <= startX + 3) || (x >= endX - 3 && x <= endX + HandleWidth + 2);
            Cursor = onHandle ? Cursors.SizeWE : Cursors.Hand;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == DragPart.None)
        {
            return;
        }

        MoveTo(e.GetPosition(this).X, final: true);
        _drag = DragPart.None;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag != DragPart.None)
        {
            _drag = DragPart.None;
            Seek?.Invoke(Position, true);
        }
    }

    private void MoveTo(double x, bool final)
    {
        var seconds = ToSeconds(x + _grabOffset);
        switch (_drag)
        {
            case DragPart.Start:
                Start = Math.Min(seconds, End - MinimumLength);
                seconds = Start;
                break;
            case DragPart.End:
                End = Math.Max(seconds, Start + MinimumLength);
                seconds = End;
                break;
        }

        Seek?.Invoke(Math.Max(0, seconds), final);
    }

    private static DependencyProperty Register(string name, double value, bool twoWay = false) =>
        DependencyProperty.Register(name, typeof(double), typeof(TrimTimeline), new FrameworkPropertyMetadata(value,
            FrameworkPropertyMetadataOptions.AffectsRender | (twoWay ? FrameworkPropertyMetadataOptions.BindsTwoWayByDefault : 0)));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
