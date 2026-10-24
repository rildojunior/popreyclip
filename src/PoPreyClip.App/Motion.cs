using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PoPreyClip.App;

/// <summary>
/// Short entrance for pages, banners and notices: a fade with a small rise, 180 ms ease-out. It only runs while the
/// element appears, animates only opacity and position (cheap to draw), and is skipped when Windows animations are
/// off (Settings → Accessibility → Visual effects).
/// </summary>
public static class Motion
{
    private static readonly Duration EnterTime = TimeSpan.FromMilliseconds(180);
    private static readonly IEasingFunction EaseOut = Frozen(new QuarticEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Distance in pixels the element rises while it appears; 0 turns the entrance off.</summary>
    public static readonly DependencyProperty EnterOffsetProperty = DependencyProperty.RegisterAttached(
        "EnterOffset", typeof(double), typeof(Motion), new PropertyMetadata(0.0, OnEnterOffsetChanged));

    public static double GetEnterOffset(DependencyObject element) => (double)element.GetValue(EnterOffsetProperty);

    public static void SetEnterOffset(DependencyObject element, double value) => element.SetValue(EnterOffsetProperty, value);

    private static void OnEnterOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.IsVisibleChanged -= OnIsVisibleChanged;
        if ((double)e.NewValue != 0)
        {
            element.IsVisibleChanged += OnIsVisibleChanged;
        }
    }

    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is FrameworkElement element && SystemParameters.ClientAreaAnimation)
        {
            Enter(element, GetEnterOffset(element));
        }
    }

    private static void Enter(FrameworkElement element, double offset)
    {
        if (element.RenderTransform is not TranslateTransform move || move.IsFrozen)
        {
            move = new TranslateTransform();
            element.RenderTransform = move;
        }

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, EnterTime) { EasingFunction = EaseOut });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, 0, EnterTime) { EasingFunction = EaseOut });
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
