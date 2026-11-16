using System.Runtime.InteropServices;
using PoPreyClip.Native;

namespace PoPreyClip.Hotkeys;

/// <summary>One key going down or up, as seen by <see cref="RawKeyboard"/>.</summary>
/// <param name="Time">Milliseconds since boot, from the message time.</param>
internal readonly record struct RawKeyEvent(uint VirtualKey, bool IsDown, uint Time);

/// <summary>
/// Reads every key press in the system through Raw Input on one hidden message-only window, shared by all hotkeys.
/// Windows only sends a copy of each key here after the fact. Nothing sits in the path of the key on its way to the
/// game, so it adds no delay, and Windows has no reason to switch it off (unlike a low-level keyboard hook, which
/// Windows removes without warning when it answers late). The key still reaches the game and every other program.
/// One registration per process: Raw Input keeps only the last keyboard registration of a process.
/// </summary>
internal static class RawKeyboard
{
    private const uint WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeKeyboard = 1;
    private const uint RidevInputSink = 0x00000100;
    private const ushort RiKeyBreak = 0x01;
    private const ushort VkFake = 0xFF;
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly int HeaderSize = Marshal.SizeOf<RawInputHeader>();

    private static readonly object Gate = new();
    private static volatile Action<RawKeyEvent>[] _listeners = [];
    private static Thread? _thread;
    private static string? _startError;

    /// <summary>Starts listening (once per process) and adds the listener. Returns an error message, or null.</summary>
    public static string? Subscribe(Action<RawKeyEvent> listener)
    {
        lock (Gate)
        {
            if (_thread is null)
            {
                Start();
            }

            if (_startError is not null)
            {
                return _startError;
            }

            _listeners = [.. _listeners, listener];
            return null;
        }
    }

    public static void Unsubscribe(Action<RawKeyEvent> listener)
    {
        lock (Gate)
        {
            _listeners = _listeners.Where(l => l != listener).ToArray();
        }
    }

    private static void Start()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Name = "PoPreyClip raw keyboard",
        };
        _thread.Start();
        ready.Wait();
    }

    private static void Run(ManualResetEventSlim ready)
    {
        // Answers within milliseconds of a key press even while Windows slows down the app in the background.
        Win32.KeepThreadResponsive();

        // "Message" is the system class for message-only windows: invisible, no taskbar entry, no window procedure of ours.
        var window = CreateWindowExW(0, "Message", "PoPreyClip raw keyboard", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            _startError = $"Não foi possível ler o teclado (erro {Marshal.GetLastWin32Error()} ao criar a janela).";
            ready.Set();
            return;
        }

        var device = new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RidevInputSink, Target = window };
        if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            _startError = $"Não foi possível ler o teclado (erro {Marshal.GetLastWin32Error()}).";
            DestroyWindow(window);
            ready.Set();
            return;
        }

        ready.Set();
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.Message == WmInput && ReadKey(msg.LParam, buffer) is { } key)
                {
                    var rawEvent = new RawKeyEvent(key.VirtualKey, key.IsDown, msg.Time);
                    foreach (var listener in _listeners)
                    {
                        listener(rawEvent);
                    }
                }

                // Lets Windows free the input data (DefWindowProc) after WM_INPUT.
                DispatchMessageW(ref msg);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (uint VirtualKey, bool IsDown)? ReadKey(IntPtr rawInput, IntPtr buffer)
    {
        var size = 256u;
        if (GetRawInputData(rawInput, RidInput, buffer, ref size, (uint)HeaderSize) == uint.MaxValue)
        {
            return null;
        }

        var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
        if (header.Type != RimTypeKeyboard)
        {
            return null;
        }

        var keyboard = Marshal.PtrToStructure<RawKeyboardData>(buffer + HeaderSize);
        if (keyboard.VirtualKey is 0 or VkFake)
        {
            return null;
        }

        return (keyboard.VirtualKey, (keyboard.Flags & RiKeyBreak) == 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboardData
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Win32.Msg msg);
}
