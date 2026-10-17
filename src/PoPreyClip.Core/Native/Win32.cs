using System.Runtime.InteropServices;
using System.Text;

namespace PoPreyClip.Native;

internal static class Win32
{
    public const uint WmHotkey = 0x0312;
    public const uint WmQuit = 0x0012;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint Synchronize = 0x00100000;
    public const uint WaitTimeout = 0x00000102;
    private const uint Th32csSnapProcess = 0x00000002;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    public readonly record struct ProcessInfo(int ProcessId, int ParentProcessId, string ExeName);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    /// <summary>True when the window is minimized.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out Msg msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var builder = new StringBuilder(512);
        _ = GetWindowTextW(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    /// <summary>Bounds, in screen pixels, of the monitor that shows most of the window (the nearest one if none).</summary>
    public static Rect GetMonitorBounds(IntPtr hwnd)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfoW(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info) ? info.Monitor : default;
    }

    /// <summary>True when the window covers its whole monitor (exclusive fullscreen or borderless windowed).</summary>
    public static bool IsFullscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == GetShellWindow() || hwnd == GetDesktopWindow())
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out var window))
        {
            return false;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return false;
        }

        return window.Left <= info.Monitor.Left && window.Top <= info.Monitor.Top &&
               window.Right >= info.Monitor.Right && window.Bottom >= info.Monitor.Bottom;
    }

    /// <summary>
    /// Main window of an app: the foreground window when it belongs to one of <paramref name="processIds"/>,
    /// otherwise the largest visible, unowned top-level window of those processes. Zero when there is none.
    /// </summary>
    public static IntPtr FindMainWindow(IReadOnlySet<int> processIds)
    {
        var foreground = GetForegroundWindow();
        if (IsCandidateWindow(foreground, processIds))
        {
            return foreground;
        }

        var best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((hwnd, _) =>
        {
            if (IsCandidateWindow(hwnd, processIds) && GetWindowRect(hwnd, out var rect))
            {
                var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area > bestArea)
                {
                    best = hwnd;
                    bestArea = area;
                }
            }

            return true;
        }, IntPtr.Zero);
        return best;
    }

    private static bool IsCandidateWindow(IntPtr hwnd, IReadOnlySet<int> processIds)
    {
        const uint GwOwner = 4;
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        if (!processIds.Contains((int)pid) || !GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        // Skip tiny helper windows; splash screens are replaced later by the capture watchdog.
        return rect.Right - rect.Left >= 320 && rect.Bottom - rect.Top >= 200;
    }

    /// <summary>A process and all of its descendants (games often draw from a child process of the launcher exe).</summary>
    public static HashSet<int> ProcessTree(IReadOnlyList<ProcessInfo> processes, int rootId)
    {
        var tree = new HashSet<int> { rootId };
        bool added;
        do
        {
            added = false;
            foreach (var process in processes)
            {
                if (!tree.Contains(process.ProcessId) && tree.Contains(process.ParentProcessId) && process.ProcessId != process.ParentProcessId)
                {
                    tree.Add(process.ProcessId);
                    added = true;
                }
            }
        }
        while (added);
        return tree;
    }

    /// <summary>Full image path of a process. Works for most elevated or protected game processes too.</summary>
    public static string? GetProcessPath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = 1024u;
            var builder = new StringBuilder((int)size);
            return QueryFullProcessImageNameW(handle, 0, builder, ref size) ? builder.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static bool IsProcessAlive(int processId)
    {
        var handle = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return WaitForSingleObject(handle, 0) == WaitTimeout;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Snapshot of every running process with its parent id.</summary>
    public static List<ProcessInfo> GetProcesses()
    {
        var result = new List<ProcessInfo>(512);
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result.Add(new ProcessInfo((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    /// <summary>
    /// Finds the top-most process of an app with the given executable name, e.g. the main Discord.exe
    /// whose children (voice, renderer) share its name. Capturing its process tree gets every child.
    /// </summary>
    public static int? FindRootProcess(IReadOnlyList<ProcessInfo> processes, string exeName)
    {
        var matches = processes.Where(p => string.Equals(p.ExeName, exeName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        var ids = matches.Select(p => p.ProcessId).ToHashSet();
        var roots = matches.Where(p => !ids.Contains(p.ParentProcessId)).OrderBy(p => p.ProcessId).ToList();
        return (roots.Count > 0 ? roots[0] : matches[0]).ProcessId;
    }
}
