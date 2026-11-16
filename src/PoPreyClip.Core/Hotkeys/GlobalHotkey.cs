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

    /// <summary>Extra modifiers players often hold while pressing a hotkey (sprint, crouch, walk, push-to-talk).</summary>
    public const uint LooseModifiers = ModControl | ModShift | ModAlt;

    /// <summary>
    /// True when the held modifiers include every modifier of this binding plus, at most, Ctrl, Shift or Alt.
    /// So "F9" still works while the player is running with Shift or holding Alt, but not with Win held.
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
/// System-wide hotkey. It first asks Windows to reserve the key (RegisterHotKey) on a dedicated message-loop thread.
/// Presses with extra Shift, Ctrl or Alt held (running, crouching, push-to-talk) do not fire a reserved hotkey, so
/// they come from the shared <see cref="RawKeyboard"/> listener, which only receives a copy of each key after the
/// fact and never delays it (see <see cref="HotkeyBinding.AcceptsLoosely"/>).
/// F12 alone cannot be reserved (Windows keeps it for debuggers) but is still delivered to every program, so it is
/// shared through the listener: one press also reaches Steam, whose overlay reads F12 inside the game for its own
/// screenshot. Any other key that cannot be reserved belongs to another program, which swallows it before Raw Input
/// sees it, so the user is told to pick another key.
/// No low-level keyboard hook: Windows silently removes a hook that answers late (a garbage collection or a PC busy
/// with a game is enough), which made F12 stop working during long sessions.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x5150;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private const uint VkF12 = 0x7B;

    /// <summary>
    /// Longer than the slowest key repeat Windows allows (1 s before the first repeat, then at most ~400 ms apart).
    /// A key-down that arrives later than this after the previous one is a new press, even if the key-up was lost.
    /// </summary>
    private const uint NewPressGapMs = 1_200;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly HotkeyBinding _binding;
    private readonly Func<uint, uint, bool>? _isTakenElsewhere;
    private readonly Action<RawKeyEvent> _onRawKey;
    private bool _listening;
    private bool _keyHeld;
    private uint _lastKeyDownTime;
    private bool _registered;
    private uint _threadId;
    private string? _error;

    /// <param name="isTakenElsewhere">
    /// Receives a virtual key and the held modifiers; returns true when another PoPreyClip hotkey owns that exact
    /// combination, so this one does not also fire (for example "F9" must not react to "Ctrl+F9" if that saves
    /// something else).
    /// </param>
    public GlobalHotkey(HotkeyBinding binding, Action onPressed, Func<uint, uint, bool>? isTakenElsewhere = null)
    {
        _binding = binding;
        _isTakenElsewhere = isTakenElsewhere;
        _onRawKey = key => OnKey(key.VirtualKey, key.IsDown, key.Time);
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

    /// <summary>True for F12, which PoPreyClip shares with other programs instead of reserving it.</summary>
    public bool IsShared => !_registered && _listening;

    /// <summary>F12 without modifiers: refused by RegisterHotKey because of the debugger, yet delivered to everyone.</summary>
    private bool IsKeptByWindows => _binding.VirtualKey == VkF12 && _binding.Modifiers == 0;

    private void Run()
    {
        _threadId = Win32.GetCurrentThreadId();
        var registered = Win32.RegisterHotKey(IntPtr.Zero, HotkeyId, _binding.Modifiers | HotkeyBinding.ModNoRepeat, _binding.VirtualKey);
        _registered = registered;
        if (!registered)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorHotkeyAlreadyRegistered && IsKeptByWindows && Listen() is null)
            {
                Log.Info($"O atalho {_binding.Text} é compartilhado com outros programas (como a captura de tela da Steam).");
            }
            else
            {
                _error = error == ErrorHotkeyAlreadyRegistered
                    ? $"O atalho {_binding.Text} já é usado por outro programa. Escolha outra tecla."
                    : $"Não foi possível usar o atalho {_binding.Text} (erro {error}).";
                _ready.Set();
                return;
            }
        }
        else if (Listen() is not null)
        {
            // The exact combination still works through RegisterHotKey.
            Log.Warn($"O atalho {_binding.Text} só vai funcionar sem outras teclas como Shift, Ctrl ou Alt seguradas.");
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

            if (_listening)
            {
                RawKeyboard.Unsubscribe(_onRawKey);
            }
        }
    }

    private string? Listen()
    {
        var error = RawKeyboard.Subscribe(_onRawKey);
        _listening = error is null;
        if (error is not null)
        {
            Log.Warn(error);
        }

        return error;
    }

    /// <summary>Called on the shared keyboard thread for every key going down or up; must return quickly.</summary>
    private void OnKey(uint virtualKey, bool isDown, uint time)
    {
        if (virtualKey != _binding.VirtualKey)
        {
            return;
        }

        if (!isDown)
        {
            _keyHeld = false;
            return;
        }

        // Ignore auto-repeat while the key is held down. A long gap means the key-up was lost (for example the key
        // was released on another desktop), and that must not block the key forever.
        var isNewPress = !_keyHeld || time - _lastKeyDownTime > NewPressGapMs;
        _keyHeld = true;
        _lastKeyDownTime = time;
        if (isNewPress && ShouldFire(CurrentModifiers()))
        {
            ThreadPool.QueueUserWorkItem(_ => OnPressed());
        }
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

        // Logging touches the disk, so it stays off the keyboard thread.
        ThreadPool.QueueUserWorkItem(_ => Log.Debug($"Atalho {_binding.Text} reconhecido com Shift, Ctrl ou Alt segurado."));
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
