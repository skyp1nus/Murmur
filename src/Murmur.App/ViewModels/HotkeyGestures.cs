using Avalonia.Input;

namespace Murmur.App.ViewModels;

public enum HotkeyProblem
{
    None,

    /// <summary>A modifier key on its own: keep waiting for the real key.</summary>
    ModifierOnly,

    /// <summary>A plain key would be swallowed system-wide: Ctrl, Alt or Win is required (function keys excepted).</summary>
    NeedsModifier,

    /// <summary>The combination does not survive a <see cref="KeyGesture.Parse(string)"/> round trip.</summary>
    Unsupported,
}

/// <summary>Turns key presses into hotkey strings in Avalonia gesture syntax (<c>Ctrl+Shift+R</c>) and back.</summary>
public static class HotkeyGestures
{
    /// <summary>The gesture text of a key press, validated by parsing it back; <see langword="null"/> with the reason in <paramref name="problem"/> when it cannot be used.</summary>
    public static string? TryFormat(Key key, KeyModifiers modifiers, out HotkeyProblem problem)
    {
        problem = HotkeyProblem.None;
        if (IsModifierKey(key) || key == Key.None)
        {
            problem = HotkeyProblem.ModifierOnly;
            return null;
        }

        // The platform modifier keys; Shift alone only produces capital letters.
        const KeyModifiers Strong = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta;
        if ((modifiers & Strong) == 0 && !IsFunctionKey(key))
        {
            problem = HotkeyProblem.NeedsModifier;
            return null;
        }

        var text = new KeyGesture(key, modifiers).ToString();
        if (!TryParse(text, out var parsed) || parsed.Key != key || parsed.KeyModifiers != modifiers)
        {
            problem = HotkeyProblem.Unsupported;
            return null;
        }

        return text;
    }

    /// <summary>Parses a stored hotkey; <see langword="false"/> for text Avalonia does not understand.</summary>
    public static bool TryParse(string? text, out KeyGesture gesture)
    {
        gesture = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            gesture = KeyGesture.Parse(text);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The same shortcut written differently ("ctrl+shift+r", "Shift+Ctrl+R") compares equal.</summary>
    public static bool Same(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (TryParse(a, out var first) && TryParse(b, out var second))
        {
            return first.Key == second.Key && first.KeyModifiers == second.KeyModifiers;
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>Ctrl+Shift+R</c> as the caps shown in the mockup: <c>Ctrl Shift R</c>.</summary>
    public static string Display(string gesture) => gesture.Replace('+', ' ');

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System or Key.CapsLock or Key.NumLock or Key.Scroll;

    private static bool IsFunctionKey(Key key) => key >= Key.F1 && key <= Key.F24;
}
