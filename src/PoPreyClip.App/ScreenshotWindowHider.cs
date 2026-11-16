using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PoPreyClip.App;

/// <summary>
/// Hides PoPreyClip's own windows while a screenshot is taken from inside the app (button or tray menu), so the
/// picture shows the game behind them and not the app. The windows are cloaked (removed from the screen by the
/// window compositor, with no minimize animation and without losing focus or position) and come back right after.
/// </summary>
internal static class ScreenshotWindowHider
{
    private const int DwmwaCloak = 13;

    public static async Task<T> RunHiddenAsync<T>(Func<Task<T>> capture)
    {
        var hidden = new List<IntPtr>();
        foreach (Window window in Application.Current.Windows)
        {
            if (!window.IsVisible || window.WindowState == WindowState.Minimized)
            {
                continue;
            }

            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero && SetCloak(handle, true))
            {
                hidden.Add(handle);
            }
        }

        try
        {
            if (hidden.Count > 0)
            {
                // Wait until the compositor has drawn the screen without the windows.
                await Task.Run(() =>
                {
                    DwmFlush();
                    DwmFlush();
                });
            }

            return await capture();
        }
        finally
        {
            foreach (var handle in hidden)
            {
                SetCloak(handle, false);
            }
        }
    }

    private static bool SetCloak(IntPtr handle, bool cloak)
    {
        var value = cloak ? 1 : 0;
        return DwmSetWindowAttribute(handle, DwmwaCloak, ref value, sizeof(int)) == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
