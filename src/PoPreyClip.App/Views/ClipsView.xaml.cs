using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PoPreyClip.App.ViewModels;

namespace PoPreyClip.App.Views;

public partial class ClipsView : UserControl
{
    /// <summary>Cards are at least 280 px wide plus the 16 px gap.</summary>
    private const double MinimumCardWidth = 296;

    private Point? _dragStart;
    private Window? _window;

    public ClipsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null)
            {
                _window.PreviewKeyDown += OnWindowKeyDown;
            }
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null)
            {
                _window.PreviewKeyDown -= OnWindowKeyDown;
            }
        };
    }

    /// <summary>Recomputes how many cards fit side by side when the visible width changes.</summary>
    private void OnClipGridScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 && e.ViewportWidth > 0 && DataContext is ClipsViewModel clips)
        {
            clips.Columns = Math.Max(1, (int)(e.ViewportWidth / MinimumCardWidth));
        }
    }

    /// <summary>The share button opens the same menu as a right-click, under the button.</summary>
    private void OnShareClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>Ctrl+C copies the clip under the mouse (not while typing in the search box).</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible || e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control || Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (Mouse.DirectlyOver is DependencyObject over && FindClip(over) is { } clip)
        {
            clip.CopyCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Dragging a card drops the file in another program. The action buttons keep their clicks: a drag only starts
    // from the picture or the text, after the mouse moves a few pixels with the button held.
    private void OnCardMouseDown(object sender, MouseButtonEventArgs e) =>
        _dragStart = IsActionButton(e.OriginalSource as DependencyObject) ? null : e.GetPosition(this);

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            _dragStart = null;
            return;
        }

        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragStart = null;
        if (sender is FrameworkElement { DataContext: ClipItemViewModel clip } card)
        {
            // The play overlay is a button and may hold the mouse; release it so it does not play after the drop.
            Mouse.Captured?.ReleaseMouseCapture();
            ClipSharing.StartDrag(card, clip.Info);
            e.Handled = true;
        }
    }

    /// <summary>True inside one of the card's buttons, except the play overlay that covers the whole picture.</summary>
    private static bool IsActionButton(DependencyObject? element)
    {
        for (var current = element; current is not null; current = GetParent(current))
        {
            if (current is ButtonBase button)
            {
                return button.Name != "PlayOverlay";
            }

            if (current is Border { Name: "Card" })
            {
                return false;
            }
        }

        return false;
    }

    private static ClipItemViewModel? FindClip(DependencyObject element)
    {
        for (var current = element; current is not null; current = GetParent(current))
        {
            if (current is FrameworkElement { DataContext: ClipItemViewModel clip })
            {
                return clip;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);
}
