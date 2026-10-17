using System.Windows;
using System.Windows.Controls;
using PoPreyClip.App.ViewModels;

namespace PoPreyClip.App.Views;

public partial class AudioView : UserControl
{
    private Window? _window;

    public AudioView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => UpdateVisible();
        Loaded += (_, _) =>
        {
            if (_window is null && Window.GetWindow(this) is { } window)
            {
                _window = window;
                _window.StateChanged += (_, _) => UpdateVisible();
            }

            UpdateVisible();
        };
    }

    /// <summary>The meters (and the level preview) only run while the page can be seen.</summary>
    private void UpdateVisible() =>
        (DataContext as AudioViewModel)?.SetViewVisible(IsVisible && _window?.WindowState != WindowState.Minimized);
}
