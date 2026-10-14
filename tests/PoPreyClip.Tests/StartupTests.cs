using Microsoft.Win32;
using PoPreyClip.Startup;

namespace PoPreyClip.Tests;

public sealed class StartupTests : IDisposable
{
    // A throwaway key instead of the real Run key, so tests never touch the user's startup apps.
    private readonly string _keyPath = $@"Software\PoPreyClipTests\Run-{Guid.NewGuid():N}";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\PoPreyClipTests", throwOnMissingSubKey: false);

    [Fact]
    public void Enabling_writes_a_minimized_command_and_disabling_removes_it()
    {
        var startup = new WindowsStartup(@"C:\Apps\PoPreyClip.exe", _keyPath);
        Assert.False(startup.IsEnabled);

        startup.SetEnabled(true);
        using (var key = Registry.CurrentUser.OpenSubKey(_keyPath))
        {
            Assert.Equal("\"C:\\Apps\\PoPreyClip.exe\" --minimized", key!.GetValue(WindowsStartup.ValueName));
        }

        Assert.True(startup.IsEnabled);

        startup.SetEnabled(false);
        Assert.False(startup.IsEnabled);
    }

    [Fact]
    public void Refresh_points_a_missing_exe_to_the_running_one()
    {
        new WindowsStartup(@"C:\Nao\Existe\PoPreyClip.exe", _keyPath).SetEnabled(true);
        var here = new WindowsStartup(Environment.ProcessPath, _keyPath);

        Assert.True(here.RefreshPath());
        Assert.Equal(here.Command, Registry.CurrentUser.OpenSubKey(_keyPath)!.GetValue(WindowsStartup.ValueName));
    }

    [Fact]
    public void Refresh_keeps_an_entry_that_points_to_an_existing_copy()
    {
        var existing = new WindowsStartup(Environment.ProcessPath, _keyPath);
        existing.SetEnabled(true);

        Assert.False(new WindowsStartup(@"C:\Outra\PoPreyClip.exe", _keyPath).RefreshPath());
        Assert.Equal(existing.Command, Registry.CurrentUser.OpenSubKey(_keyPath)!.GetValue(WindowsStartup.ValueName));
    }

    [Fact]
    public void Refresh_does_nothing_when_disabled()
    {
        Assert.False(new WindowsStartup(@"C:\Apps\PoPreyClip.exe", _keyPath).RefreshPath());
        Assert.False(new WindowsStartup(@"C:\Apps\PoPreyClip.exe", _keyPath).IsEnabled);
    }

    [Theory]
    [InlineData("\"C:\\a b\\PoPreyClip.exe\" --minimized", "C:\\a b\\PoPreyClip.exe")]
    [InlineData("C:\\Apps\\PoPreyClip.exe --minimized", "C:\\Apps\\PoPreyClip.exe")]
    [InlineData("C:\\Apps\\PoPreyClip.exe", "C:\\Apps\\PoPreyClip.exe")]
    public void Executable_is_read_from_the_command_line(string command, string expected)
    {
        Assert.Equal(expected, WindowsStartup.ExecutableOf(command));
    }
}
