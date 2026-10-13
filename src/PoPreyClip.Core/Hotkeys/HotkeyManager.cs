namespace PoPreyClip.Hotkeys;

/// <summary>Named global hotkeys that can be replaced at any time (for example from the settings screen).</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly Dictionary<string, (string Text, GlobalHotkey Hotkey)> _hotkeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Text, Action OnPressed)> _suspended = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _isSuspended;

    /// <summary>Raised with the hotkey name and a message when Windows refuses a registration.</summary>
    public event Action<string, string>? RegistrationFailed;

    /// <summary>Temporarily releases every hotkey (for example while the user records a new one).</summary>
    public void Suspend()
    {
        lock (_gate)
        {
            foreach (var (name, entry) in _hotkeys)
            {
                _suspended[name] = (entry.Text, entry.Hotkey.OnPressed);
                entry.Hotkey.Dispose();
            }

            _hotkeys.Clear();
            _isSuspended = true;
        }
    }

    public void Resume()
    {
        List<KeyValuePair<string, (string Text, Action OnPressed)>> pending;
        lock (_gate)
        {
            _isSuspended = false;
            pending = _suspended.ToList();
            _suspended.Clear();
        }

        foreach (var (name, entry) in pending)
        {
            Set(name, entry.Text, entry.OnPressed);
        }
    }

    /// <summary>Registers or replaces a hotkey. Empty text removes it. Returns an error message, or null on success.</summary>
    public string? Set(string name, string? text, Action onPressed)
    {
        lock (_gate)
        {
            if (_isSuspended)
            {
                _suspended.Remove(name);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                try
                {
                    HotkeyBinding.Parse(text);
                }
                catch (FormatException ex)
                {
                    return ex.Message;
                }

                _suspended[name] = (text, onPressed);
                return null;
            }

            if (_hotkeys.TryGetValue(name, out var existing))
            {
                if (string.Equals(existing.Text, text, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                existing.Hotkey.Dispose();
                _hotkeys.Remove(name);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                _hotkeys[name] = (text, new GlobalHotkey(HotkeyBinding.Parse(text), onPressed));
                return null;
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                Log.Warn(ex.Message);
                RegistrationFailed?.Invoke(name, ex.Message);
                return ex.Message;
            }
        }
    }

    /// <summary>Registers a single hotkey named after its key combination.</summary>
    public string? Register(string text, Action onPressed) => Set(text, text, onPressed);

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var (_, hotkey) in _hotkeys.Values)
            {
                hotkey.Dispose();
            }

            _hotkeys.Clear();
        }
    }
}
