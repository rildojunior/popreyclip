using System.Runtime.InteropServices;
using PoPreyClip.Native;

namespace PoPreyClip.Hotkeys;

public readonly record struct HotkeyBinding(uint Modifiers, uint VirtualKey, string Text)
{
    public const uint ModAlt = 0x1;
    public const uint ModControl = 0x2;
    public const uint ModShift = 0x4;
    public const uint ModWin = 0x8;
    public const uint ModNoRepeat = 0x4000;

    /// <summary>Extra modifiers players often hold while pressing a hotkey (sprint, crouch, walk).</summary>
    public const uint LooseModifiers = ModControl | ModShift;

    /// <summary>
    /// True when the held modifiers include every modifier of this binding plus, at most, Ctrl or Shift.
    /// So "F9" still works while the player is running with Shift held, but not with Alt or Win held.
    /// </summary>
    public bool AcceptsLoosely(uint heldModifiers) =>
        (heldModifiers & Modifiers) == Modifiers && (heldModifiers & ~Modifiers & ~LooseModifiers) == 0;

    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PrintScreen"] = 0x2C, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Pause"] = 0x13, ["ScrollLock"] = 0x91,
    };

    /// <summary>Parses "F8", "Ctrl+Shift+F9", "Alt+C", "Ctrl+NumPad5".</summary>
    public static HotkeyBinding Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException("Atalho vazio.");
        }

        uint modifiers = 0;
        uint? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModControl;
                    continue;
                case "alt":
                    modifiers |= ModAlt;
                    continue;
                case "shift":
                    modifiers |= ModShift;
                    continue;
                case "win" or "windows":
                    modifiers |= ModWin;
                    continue;
            }

            if (key is not null)
            {
                throw new FormatException($"Atalho com mais de uma tecla principal: {text}");
            }

            key = ParseKey(raw) ?? throw new FormatException($"Tecla desconhecida no atalho: {raw}");
        }

        return key is null
            ? throw new FormatException($"Atalho sem tecla principal: {text}")
            : new HotkeyBinding(modifiers, key.Value, text);
    }

    private static uint? ParseKey(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return char.ToUpperInvariant(key[0]);
        }

        if (key.Length > 1 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24)
        {
            return (uint)(0x70 + f - 1);
        }

        if (key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[6..], out var n) && n is >= 0 and <= 9)
        {
            return (uint)(0x60 + n);
        }

        return NamedKeys.TryGetValue(key, out var vk) ? vk : null;
    }
}

/// <summary>
/// System-wide hotkey on a dedicated message-loop thread. It first asks Windows to reserve the key
/// (RegisterHotKey). If another program already reserved it (error 1409, common for F12), it falls back to
/// a shared low-level keyboard hook, which is how Medal reads its hotkeys: the key still reaches the other
/// program and PoPreyClip reacts too.
/// The hook is also installed alongside RegisterHotKey, because Windows only fires a reserved hotkey when
/// the held modifiers match exactly: pressing F9 while running with Shift held would be ignored. The hook
/// accepts those presses too (see <see cref="HotkeyBinding.AcceptsLoosely"/>) without taking the
/// combination away from other programs.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x5150;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly HotkeyBinding _binding;
    private readonly Func<uint, uint, bool>? _isTakenElsewhere;
    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hook;
    private bool _keyHeld;
    private bool _registered;
    private uint _threadId;
    private string? _error;

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    /// <param name="isTakenElsewhere">
    /// Receives a virtual key and the held modifiers; returns true when another PoPreyClip hotkey owns that exact
    /// combination, so this one does not also fire (for example "F9" must not react to "Ctrl+F9" if that saves
    /// something else).
    /// </param>
    public GlobalHotkey(HotkeyBinding binding, Action onPressed, Func<uint, uint, bool>? isTakenElsewhere = null)
    {
        _binding = binding;
        _isTakenElsewhere = isTakenElsewhere;
        OnPressed = onPressed;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PoPreyClip hotkey",
        };
        _thread.Start();
        _ready.Wait();
        if (_error is not null)
        {
            throw new InvalidOperationException(_error);
        }
    }

    public Action OnPressed { get; }

    /// <summary>True when the key is shared with another program through the keyboard hook.</summary>
    public bool IsShared => !_registered && _hook != IntPtr.Zero;

    private void Run()
    {
        _threadId = Win32.GetCurrentThreadId();
        var registered = Win32.RegisterHotKey(IntPtr.Zero, HotkeyId, _binding.Modifiers | HotkeyBinding.ModNoRepeat, _binding.VirtualKey);
        _registered = registered;
        if (!registered)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorHotkeyAlreadyRegistered || !InstallHook())
            {
                _error = $"Não foi possível usar o atalho {_binding.Text} (erro {error}).";
                _ready.Set();
                return;
            }

            Log.Info($"O atalho {_binding.Text} já é usado por outro programa. O PoPreyClip vai compartilhar a tecla com ele.");
        }
        else if (!InstallHook())
        {
            // The exact combination still works through RegisterHotKey.
            Log.Warn($"O atalho {_binding.Text} só vai funcionar sem outras teclas como Shift ou Ctrl seguradas.");
        }

        _ready.Set();
        try
        {
            while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.Message == Win32.WmHotkey && (int)msg.WParam.ToUInt32() == HotkeyId)
                {
                    ThreadPool.QueueUserWorkItem(_ => OnPressed());
                }
            }
        }
        finally
        {
            if (registered)
            {
                Win32.UnregisterHotKey(IntPtr.Zero, HotkeyId);
            }

            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
            }
        }
    }

    private bool InstallHook()
    {
        _hookProc = HookCallback;
        _hook = SetWindowsHookExW(WhKeyboardLl, _hookProc, GetModuleHandleW(null), 0);
        return _hook != IntPtr.Zero;
    }

    /// <summary>Runs on the hotkey thread for every key event; must return quickly.</summary>
    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var virtualKey = (uint)Marshal.ReadInt32(lParam);
            var message = (int)wParam;
            if (virtualKey == _binding.VirtualKey)
            {
                if (message is WmKeyDown or WmSysKeyDown)
                {
                    // Ignore auto-repeat while the key is held down.
                    if (!_keyHeld && ShouldFire(CurrentModifiers()))
                    {
                        ThreadPool.QueueUserWorkItem(_ => OnPressed());
                    }

                    _keyHeld = true;
                }
                else if (message is WmKeyUp or WmSysKeyUp)
                {
                    _keyHeld = false;
                }
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private bool ShouldFire(uint heldModifiers)
    {
        if (heldModifiers == _binding.Modifiers)
        {
            // When Windows reserved the key for us, the exact press already arrives as WM_HOTKEY.
            return !_registered;
        }

        if (!_binding.AcceptsLoosely(heldModifiers) || _isTakenElsewhere?.Invoke(_binding.VirtualKey, heldModifiers) == true)
        {
            return false;
        }

        // Logging touches the disk, so it stays off the hook thread.
        ThreadPool.QueueUserWorkItem(_ => Log.Debug($"Atalho {_binding.Text} reconhecido com Shift ou Ctrl segurado."));
        return true;
    }

    private static uint CurrentModifiers()
    {
        static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        uint modifiers = 0;
        if (Down(0x11))
        {
            modifiers |= HotkeyBinding.ModControl;
        }

        if (Down(0x12))
        {
            modifiers |= HotkeyBinding.ModAlt;
        }

        if (Down(0x10))
        {
            modifiers |= HotkeyBinding.ModShift;
        }

        if (Down(0x5B) || Down(0x5C))
        {
            modifiers |= HotkeyBinding.ModWin;
        }

        return modifiers;
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            Win32.PostThreadMessageW(_threadId, Win32.WmQuit, UIntPtr.Zero, IntPtr.Zero);
            _thread.Join(1000);
        }

        _ready.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int hookId, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}
