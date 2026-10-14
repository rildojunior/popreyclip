using Microsoft.Win32;

namespace PoPreyClip.Startup;

/// <summary>
/// "Iniciar com o Windows": a value under HKCU\...\CurrentVersion\Run that starts the app minimized to the tray
/// at logon. The registry is the source of truth (no config flag), so the setting also follows what the user
/// does in Task Manager → Startup apps. Per user, no admin rights needed.
/// </summary>
public sealed class WindowsStartup
{
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "PoPreyClip";
    public const string StartupArgument = "--minimized";

    private readonly string _keyPath;
    private readonly string _executable;

    public WindowsStartup(string? executable = null, string keyPath = DefaultKeyPath)
    {
        _executable = executable ?? Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unknown.");
        _keyPath = keyPath;
    }

    /// <summary>Command line written to the Run key.</summary>
    public string Command => $"\"{_executable}\" {StartupArgument}";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
            return key?.GetValue(ValueName) is string { Length: > 0 };
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// If start with Windows is on but the registered file no longer exists (the exe was moved or renamed),
    /// point it here. Another existing copy (e.g. a dev build) never steals the entry. Returns true when rewritten.
    /// </summary>
    public bool RefreshPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        if (key?.GetValue(ValueName) is not string { Length: > 0 } current
            || string.Equals(current, Command, StringComparison.OrdinalIgnoreCase)
            || File.Exists(ExecutableOf(current)))
        {
            return false;
        }

        key.SetValue(ValueName, Command, RegistryValueKind.String);
        return true;
    }

    /// <summary>Executable part of a Run command line ("C:\a b\x.exe" --arg → C:\a b\x.exe).</summary>
    internal static string ExecutableOf(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 0 ? command[1..end] : command.Trim('"');
        }

        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }
}
