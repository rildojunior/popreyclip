using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PoPreyClip.App.Controls;

/// <summary>
/// Crop frame drawn over the editor's video. <see cref="Crop"/> is normalized (0 to 1) to the video, so it does not
/// depend on the preview size. With an <see cref="Aspect"/> the frame keeps that shape; with 0 every side moves freely.
/// </summary>
public sealed class CropOverlay : FrameworkElement
{
    public static readonly DependencyProperty SourceWidthProperty = Register(nameof(SourceWidth), 16.0);
    public static readonly DependencyProperty SourceHeightProperty = Register(nameof(SourceHeight), 9.0);
    public static readonly DependencyProperty AspectProperty = Register(nameof(Aspect), 0.0);

    public static readonly DependencyProperty CropProperty = DependencyProperty.Register(
        nameof(Crop), typeof(Rect), typeof(CropOverlay),
        new FrameworkPropertyMetadata(new Rect(0, 0, 1, 1), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty IsCroppingProperty = DependencyProperty.Register(
        nameof(IsCropping), typeof(bool), typeof(CropOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((CropOverlay)d).IsHitTestVisible = (bool)d.GetValue(IsCroppingProperty)));

    /// <summary>Smallest crop side, in source pixels.</summary>
    private const double MinimumSide = 96;
    private const double CornerGrab = 16;
    private const double EdgeGrab = 9;

    private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xA8, 0x05, 0x06, 0x0A)));
    private static readonly Pen FramePen = Frozen(new Pen(Brushes.White, 1.5));
    private static readonly Pen GuidePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen HandlePen = Frozen(new Pen(Brushes.White, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    private Part _drag = Part.None;
    private Point _dragStart;
    private Rect _cropAtStart;

    public CropOverlay()
    {
        IsHitTestVisible = false;
    }

    [Flags]
    private enum Part
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
        Move = 16,
    }

    public double SourceWidth
    {
        get => (double)GetValue(SourceWidthProperty);
        set => SetValue(SourceWidthProperty, value);
    }

    public double SourceHeight
    {
        get => (double)GetValue(SourceHeightProperty);
        set => SetValue(SourceHeightProperty, value);
    }

    /// <summary>Width divided by height of the crop in source pixels, or 0 for a free shape.</summary>
    public double Aspect
    {
        get => (double)GetValue(AspectProperty);
        set => SetValue(AspectProperty, value);
    }

    public Rect Crop
    {
        get => (Rect)GetValue(CropProperty);
        set => SetValue(CropProperty, value);
    }

    public bool IsCropping
    {
        get => (bool)GetValue(IsCroppingProperty);
        set => SetValue(IsCroppingProperty, value);
    }

    /// <summary>Where the video is drawn (the image uses Stretch="Uniform").</summary>
    private Rect VideoRect
    {
        get
        {
            var scale = Math.Min(ActualWidth / SourceWidth, ActualHeight / SourceHeight);
            var width = SourceWidth * scale;
            var height = SourceHeight * scale;
            return new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
        }
    }

    private Rect CropOnScreen
    {
        get
        {
            var video = VideoRect;
            var crop = Crop;
            return new Rect(video.X + crop.X * video.Width, video.Y + crop.Y * video.Height, crop.Width * video.Width, crop.Height * video.Height);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!IsCropping || SourceWidth <= 0 || SourceHeight <= 0)
        {
            return;
        }

        // Transparent fill so the whole area receives the mouse.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var video = VideoRect;
        var crop = CropOnScreen;
        dc.DrawRectangle(DimBrush, null, new Rect(video.Left, video.Top, video.Width, Math.Max(0, crop.Top - video.Top)));
        dc.DrawRectangle(DimBrush, null, new Rect(video.Left, crop.Bottom, video.Width, Math.Max(0, video.Bottom - crop.Bottom)));
        dc.DrawRectangle(DimBrush, null, new Rect(video.Left, crop.Top, Math.Max(0, crop.Left - video.Left), crop.Height));
        dc.DrawRectangle(DimBrush, null, new Rect(crop.Right, crop.Top, Math.Max(0, video.Right - crop.Right), crop.Height));

        for (var i = 1; i <= 2; i++)
        {
            var x = crop.Left + crop.Width * i / 3;
            var y = crop.Top + crop.Height * i / 3;
            dc.DrawLine(GuidePen, new Point(x, crop.Top), new Point(x, crop.Bottom));
            dc.DrawLine(GuidePen, new Point(crop.Left, y), new Point(crop.Right, y));
        }

        dc.DrawRectangle(null, FramePen, crop);
        var arm = Math.Min(18, Math.Min(crop.Width, crop.Height) / 3);
        foreach (var (corner, dx, dy) in new[]
                 {
                     (crop.TopLeft, 1, 1), (crop.TopRight, -1, 1), (crop.BottomLeft, 1, -1), (crop.BottomRight, -1, -1),
                 })
        {
            dc.DrawLine(HandlePen, corner, new Point(corner.X + dx * arm, corner.Y));
            dc.DrawLine(HandlePen, corner, new Point(corner.X, corner.Y + dy * arm));
        }

        if (Aspect <= 0)
        {
            var half = Math.Min(10, crop.Width / 6);
            var halfV = Math.Min(10, crop.Height / 6);
            var center = new Point(crop.Left + crop.Width / 2, crop.Top + crop.Height / 2);
            dc.DrawLine(HandlePen, new Point(center.X - half, crop.Top), new Point(center.X + half, crop.Top));
            dc.DrawLine(HandlePen, new Point(center.X - half, crop.Bottom), new Point(center.X + half, crop.Bottom));
            dc.DrawLine(HandlePen, new Point(crop.Left, center.Y - halfV), new Point(crop.Left, center.Y + halfV));
            dc.DrawLine(HandlePen, new Point(crop.Right, center.Y - halfV), new Point(crop.Right, center.Y + halfV));
        }
    }

    private Part HitTest(Point p)
    {
        var crop = CropOnScreen;
        var nearLeft = Math.Abs(p.X - crop.Left);
        var nearRight = Math.Abs(p.X - crop.Right);
        var nearTop = Math.Abs(p.Y - crop.Top);
        var nearBottom = Math.Abs(p.Y - crop.Bottom);
        var horizontal = nearLeft <= CornerGrab ? Part.Left : nearRight <= CornerGrab ? Part.Right : Part.None;
        var vertical = nearTop <= CornerGrab ? Part.Top : nearBottom <= CornerGrab ? Part.Bottom : Part.None;
        if (horizontal != Part.None && vertical != Part.None)
        {
            return horizontal | vertical;
        }

        if (Aspect <= 0)
        {
            var insideX = p.X > crop.Left && p.X < crop.Right;
            var insideY = p.Y > crop.Top && p.Y < crop.Bottom;
            if (insideY && nearLeft <= EdgeGrab)
            {
                return Part.Left;
            }

            if (insideY && nearRight <= EdgeGrab)
            {
                return Part.Right;
            }

            if (insideX && nearTop <= EdgeGrab)
            {
                return Part.Top;
            }

            if (insideX && nearBottom <= EdgeGrab)
            {
                return Part.Bottom;
            }
        }

        return crop.Contains(p) ? Part.Move : Part.None;
    }

    private static Cursor CursorFor(Part part) => part switch
    {
        Part.Left | Part.Top or Part.Right | Part.Bottom => Cursors.SizeNWSE,
        Part.Right | Part.Top or Part.Left | Part.Bottom => Cursors.SizeNESW,
        Part.Left or Part.Right => Cursors.SizeWE,
        Part.Top or Part.Bottom => Cursors.SizeNS,
        Part.Move => Cursors.SizeAll,
        _ => Cursors.Arrow,
    };

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _drag = HitTest(e.GetPosition(this));
        if (_drag == Part.None)
        {
            return;
        }

        _dragStart = e.GetPosition(this);
        _cropAtStart = Crop;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        if (_drag == Part.None || e.LeftButton != MouseButtonState.Pressed)
        {
            Cursor = CursorFor(HitTest(position));
            return;
        }

        Crop = Normalize(Drag(position));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _drag = Part.None;
        ReleaseMouseCapture();
    }

    /// <summary>Computes the new crop in source pixels.</summary>
    private Rect Drag(Point position)
    {
        var video = VideoRect;
        var toSource = SourceWidth / video.Width;
        var dx = (position.X - _dragStart.X) * toSource;
        var dy = (position.Y - _dragStart.Y) * toSource;
        var start = new Rect(_cropAtStart.X * SourceWidth, _cropAtStart.Y * SourceHeight, _cropAtStart.Width * SourceWidth, _cropAtStart.Height * SourceHeight);
        if (_drag == Part.Move)
        {
            var x = Math.Clamp(start.X + dx, 0, SourceWidth - start.Width);
            var y = Math.Clamp(start.Y + dy, 0, SourceHeight - start.Height);
            return new Rect(x, y, start.Width, start.Height);
        }

        var left = start.Left;
        var right = start.Right;
        var top = start.Top;
        var bottom = start.Bottom;
        if (_drag.HasFlag(Part.Left))
        {
            left = Math.Clamp(left + dx, 0, right - MinimumSide);
        }

        if (_drag.HasFlag(Part.Right))
        {
            right = Math.Clamp(right + dx, left + MinimumSide, SourceWidth);
        }

        if (_drag.HasFlag(Part.Top))
        {
            top = Math.Clamp(top + dy, 0, bottom - MinimumSide);
        }

        if (_drag.HasFlag(Part.Bottom))
        {
            bottom = Math.Clamp(bottom + dy, top + MinimumSide, SourceHeight);
        }

        if (Aspect <= 0)
        {
            return new Rect(new Point(left, top), new Point(right, bottom));
        }

        // Fixed shape: the corner opposite to the one being dragged stays put.
        var anchorX = _drag.HasFlag(Part.Left) ? start.Right : start.Left;
        var anchorY = _drag.HasFlag(Part.Top) ? start.Bottom : start.Top;
        var maxWidth = _drag.HasFlag(Part.Left) ? anchorX : SourceWidth - anchorX;
        var maxHeight = _drag.HasFlag(Part.Top) ? anchorY : SourceHeight - anchorY;
        var width = Math.Max(right - left, (bottom - top) * Aspect);
        width = Math.Min(width, Math.Min(maxWidth, maxHeight * Aspect));
        width = Math.Max(width, Math.Min(MinimumSide, Math.Min(maxWidth, maxHeight * Aspect)));
        var height = width / Aspect;
        var x0 = _drag.HasFlag(Part.Left) ? anchorX - width : anchorX;
        var y0 = _drag.HasFlag(Part.Top) ? anchorY - height : anchorY;
        return new Rect(x0, y0, width, height);
    }

    private Rect Normalize(Rect source) =>
        new(source.X / SourceWidth, source.Y / SourceHeight, source.Width / SourceWidth, source.Height / SourceHeight);

    private static DependencyProperty Register(string name, double value) =>
        DependencyProperty.Register(name, typeof(double), typeof(CropOverlay), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
