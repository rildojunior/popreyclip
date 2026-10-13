using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PoPreyClip.Hotkeys;

namespace PoPreyClip.App.Controls;

/// <summary>
/// Text box that records a key combination when the user presses it ("Ctrl+Shift+F9").
/// Backspace clears the hotkey when <see cref="AllowEmpty"/> is set; Esc cancels.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).ShowCurrent()));

    public static readonly DependencyProperty AllowEmptyProperty = DependencyProperty.Register(
        nameof(AllowEmpty), typeof(bool), typeof(HotkeyBox), new PropertyMetadata(false));

    /// <summary>Raised when the box starts or stops listening, so global hotkeys can be paused.</summary>
    public static event Action<bool>? CapturingChanged;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        ShowCurrent();
    }

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public bool AllowEmpty
    {
        get => (bool)GetValue(AllowEmptyProperty);
        set => SetValue(AllowEmptyProperty, value);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Text = "Pressione as teclas...";
        CapturingChanged?.Invoke(true);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        ShowCurrent();
        CapturingChanged?.Invoke(false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            Keyboard.ClearFocus();
            return;
        }

        if (key == Key.Back && modifiers == ModifierKeys.None && AllowEmpty)
        {
            Hotkey = "";
            Keyboard.ClearFocus();
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            Text = Format(modifiers, null) + "...";
            return;
        }

        var name = KeyName(key);
        if (name is null)
        {
            return;
        }

        var text = Format(modifiers, name);
        try
        {
            HotkeyBinding.Parse(text);
            Hotkey = text;
            Keyboard.ClearFocus();
        }
        catch (FormatException)
        {
            Text = "Tecla não suportada";
        }
    }

    private void ShowCurrent() =>
        Text = string.IsNullOrWhiteSpace(Hotkey) ? "Nenhum (clique para definir)" : Hotkey;

    private static string Format(ModifierKeys modifiers, string? key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        if (key is not null)
        {
            parts.Add(key);
        }

        return string.Join("+", parts);
    }

    private static string? KeyName(Key key) => key switch
    {
        >= Key.F1 and <= Key.F24 => "F" + (key - Key.F1 + 1),
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "NumPad" + (key - Key.NumPad0),
        Key.Snapshot => "PrintScreen",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Pause => "Pause",
        Key.Scroll => "ScrollLock",
        _ => null,
    };
}
