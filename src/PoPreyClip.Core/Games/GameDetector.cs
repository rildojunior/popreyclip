using System.Diagnostics;
using PoPreyClip.Clips;
using PoPreyClip.Config;
using PoPreyClip.Native;

namespace PoPreyClip.Games;

public sealed record DetectedGame(int ProcessId, string ExeName, string DisplayName);

/// <summary>
/// Tracks the active game. A foreground app counts as a game when its executable is listed in the config,
/// or when it covers the whole monitor and fullscreen detection is enabled. The last game stays active while
/// the player alt-tabs, until its process exits.
/// </summary>
public sealed class GameDetector(Func<AppConfig> config)
{
    private readonly object _gate = new();
    private DetectedGame? _current;

    public DetectedGame? CurrentGame
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event Action<DetectedGame?>? GameChanged;

    public string ClipGameName => CurrentGame?.DisplayName ?? ClipPathBuilder.UnknownGame;

    public void Poll()
    {
        var cfg = config();
        DetectedGame? next = CurrentGame;
        if (next is not null && !Win32.IsProcessAlive(next.ProcessId))
        {
            next = null;
        }

        var foreground = Win32.GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            Win32.GetWindowThreadProcessId(foreground, out var pid);
            if (pid != 0 && pid != Environment.ProcessId && (next is null || next.ProcessId != (int)pid))
            {
                var candidate = TryDetect(cfg, foreground, (int)pid);
                if (candidate is not null)
                {
                    next = candidate;
                }
            }
        }

        // Games and watched apps from the list count as soon as they are running, even in the background.
        if (next is null && cfg.Games.Count > 0)
        {
            next = FindWatchedProcess(cfg, Win32.GetProcesses());
        }

        DetectedGame? previous;
        lock (_gate)
        {
            previous = _current;
            _current = next;
        }

        if (!Equals(previous, next))
        {
            if (next is null)
            {
                Log.Info($"Jogo encerrado: {previous?.DisplayName}");
            }
            else
            {
                Log.Info($"Jogo detectado: {next.DisplayName} ({next.ExeName}, PID {next.ProcessId})");
            }

            GameChanged?.Invoke(next);
        }
    }

    /// <summary>First running process whose executable is in the games / watched apps list (its root process).</summary>
    internal static DetectedGame? FindWatchedProcess(AppConfig cfg, IReadOnlyList<Win32.ProcessInfo> processes)
    {
        foreach (var (exe, name) in cfg.Games)
        {
            if (Win32.FindRootProcess(processes, exe) is { } pid && pid != Environment.ProcessId)
            {
                return new DetectedGame(pid, exe, name);
            }
        }

        return null;
    }

    private static DetectedGame? TryDetect(AppConfig cfg, IntPtr hwnd, int pid)
    {
        var path = Win32.GetProcessPath(pid);
        if (path is null)
        {
            return null;
        }

        var exe = Path.GetFileName(path);
        if (cfg.Games.TryGetValue(exe, out var knownName))
        {
            return new DetectedGame(pid, exe, knownName);
        }

        if (!cfg.DetectFullscreenApps ||
            cfg.IgnoredProcesses.Contains(exe, StringComparer.OrdinalIgnoreCase) ||
            path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) ||
            !Win32.IsFullscreen(hwnd))
        {
            return null;
        }

        return new DetectedGame(pid, exe, ResolveDisplayName(hwnd, path));
    }

    /// <summary>Window title first (usually the game name), then file metadata, then the executable name.</summary>
    internal static string ResolveDisplayName(IntPtr hwnd, string path)
    {
        var title = CleanName(Win32.GetWindowTitle(hwnd));
        if (title.Length is > 0 and <= 60)
        {
            return title;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var product = CleanName(info.ProductName);
            if (product.Length > 0 && !product.Contains("Unreal", StringComparison.OrdinalIgnoreCase))
            {
                return product;
            }

            var description = CleanName(info.FileDescription);
            if (description.Length > 0)
            {
                return description;
            }
        }
        catch (FileNotFoundException)
        {
        }

        return CleanName(Path.GetFileNameWithoutExtension(path).Replace("-Win64-Shipping", "", StringComparison.OrdinalIgnoreCase));
    }

    internal static string CleanName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var cleaned = value.Replace("™", "").Replace("®", "").Replace("©", "");
        return string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
