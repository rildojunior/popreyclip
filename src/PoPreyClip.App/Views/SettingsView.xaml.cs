using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PoPreyClip.App.ViewModels;

namespace PoPreyClip.App.Views;

public partial class SettingsView : UserControl
{
    private static readonly TimeSpan ScrollDuration = TimeSpan.FromMilliseconds(320);
    private SettingsViewModel? _viewModel;
    private double _scrollFrom;
    private double _scrollTo;
    private DateTime _scrollStart;

    /// <summary>
    /// True while scrolling to a clicked group. The clicked group stays highlighted, even when it is near the end of the
    /// page and can't reach the top; scrolling by hand again hands the highlight back to the page position.
    /// </summary>
    private bool _animating;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.ScrollToSectionRequested -= ScrollToSection;
            }

            _viewModel = DataContext as SettingsViewModel;
            if (_viewModel is not null)
            {
                _viewModel.ScrollToSectionRequested += ScrollToSection;
            }
        };
    }

    private IEnumerable<FrameworkElement> GroupTitles =>
        new[] { Group_geral, Group_captura, Group_atalhos, Group_avisos, Group_jogos, Group_atualizacoes };

    /// <summary>Distance from the top of the scrolled content to the group title.</summary>
    private double OffsetOf(FrameworkElement title) =>
        title.TransformToVisual((Visual)Scroller.Content).Transform(new Point(0, 0)).Y;

    private void ScrollToSection(string id)
    {
        // The page may have just become visible: wait for its layout before measuring.
        Dispatcher.InvokeAsync(() =>
        {
            var title = GroupTitles.FirstOrDefault(t => (string)t.Tag == id);
            if (title is null)
            {
                return;
            }

            _scrollFrom = Scroller.VerticalOffset;
            _scrollTo = Math.Clamp(OffsetOf(title) - 8, 0, Scroller.ScrollableHeight);
            _scrollStart = DateTime.UtcNow;
            if (!_animating)
            {
                _animating = true;
                CompositionTarget.Rendering += AnimateScroll;
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Smooth scroll, so it is clear the page moved instead of jumping.</summary>
    private void AnimateScroll(object? sender, EventArgs e)
    {
        var progress = Math.Min(1, (DateTime.UtcNow - _scrollStart) / ScrollDuration);
        var eased = 1 - Math.Pow(1 - progress, 3);
        Scroller.ScrollToVerticalOffset(_scrollFrom + (_scrollTo - _scrollFrom) * eased);
        if (progress >= 1)
        {
            CompositionTarget.Rendering -= AnimateScroll;
            Dispatcher.InvokeAsync(() => _animating = false, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>Highlights in the sidebar the group the user is reading.</summary>
    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is null || e.VerticalChange == 0 || _animating)
        {
            return;
        }

        var line = Scroller.VerticalOffset + Scroller.ViewportHeight / 3;
        var current = GroupTitles.LastOrDefault(t => OffsetOf(t) <= line) ?? Group_geral;
        if (Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1)
        {
            current = Group_atualizacoes;
        }

        _viewModel.CurrentSection = (string)current.Tag;
    }
}
