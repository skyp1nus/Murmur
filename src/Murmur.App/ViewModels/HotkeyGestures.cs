using Avalonia.Input;
using Murmur.Core.Input;

namespace Murmur.App.ViewModels;

public enum HotkeyProblem
{
    None,

    /// <summary>A modifier key on its own: keep waiting for the real key.</summary>
    ModifierOnly,

    /// <summary>A plain key would be swallowed system-wide: Ctrl, Alt or Win is required (F13..F24 excepted).</summary>
    NeedsModifier,

    /// <summary>A key that a global hotkey cannot use (see <see cref="HotkeyGesture.TryParse"/>).</summary>
    Unsupported,
}

/// <summary>Turns key presses into hotkey strings in Avalonia gesture syntax (<c>Ctrl+Shift+R</c>), validated by Core's <see cref="HotkeyGesture"/>.</summary>
public static class HotkeyGestures
{
    /// <summary>
    /// The canonical gesture text of a key press (<see cref="HotkeyGesture.ToString"/>), accepted by
    /// <see cref="HotkeyGesture.TryParse"/> - the single source of truth for what a global hotkey may be;
    /// <see langword="null"/> with the reason in <paramref name="problem"/> when it cannot be used.
    /// </summary>
    public static string? TryFormat(Key key, KeyModifiers modifiers, out HotkeyProblem problem)
    {
        problem = HotkeyProblem.None;
        if (IsModifierKey(key) || key == Key.None)
        {
            problem = HotkeyProblem.ModifierOnly;
            return null;
        }

        if (HotkeyGesture.TryParse(new KeyGesture(key, modifiers).ToString(), out var gesture))
        {
            return gesture.ToString();
        }

        // Refused: either the key is not one a global hotkey may use, or it lacks Ctrl, Alt or Win.
        const KeyModifiers Strong = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta;
        var withCtrl = new KeyGesture(key, modifiers | KeyModifiers.Control).ToString();
        problem = (modifiers & Strong) == 0 && HotkeyGesture.TryParse(withCtrl, out _)
            ? HotkeyProblem.NeedsModifier
            : HotkeyProblem.Unsupported;
        return null;
    }

    /// <summary>The same shortcut written differently ("ctrl+shift+r", "Shift+Ctrl+R") compares equal.</summary>
    public static bool Same(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (HotkeyGesture.TryParse(a, out var first) && HotkeyGesture.TryParse(b, out var second))
        {
            return first == second;
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>Ctrl+Shift+R</c> as the caps shown in the mockup: <c>Ctrl Shift R</c>. Digit and punctuation keys show as the
    /// character on the key (<c>Ctrl Shift 1</c>, <c>Ctrl ,</c>), not as their enum names (<c>D1</c>, <c>OemComma</c>).
    /// Display only: the stored text stays canonical.
    /// </summary>
    public static string Display(string gesture)
    {
        if (!HotkeyGesture.TryParse(gesture, out var parsed))
        {
            return gesture.Replace('+', ' ');
        }

        var canonical = parsed.ToString();
        var plus = canonical.LastIndexOf('+');
        var key = KeyCap(parsed.Key);
        return plus < 0 ? key : string.Concat(canonical.AsSpan(0, plus).ToString().Replace('+', ' '), " ", key);
    }

    private static string KeyCap(HotkeyKey key) => key switch
    {
        HotkeyKey.D0 => "0",
        HotkeyKey.D1 => "1",
        HotkeyKey.D2 => "2",
        HotkeyKey.D3 => "3",
        HotkeyKey.D4 => "4",
        HotkeyKey.D5 => "5",
        HotkeyKey.D6 => "6",
        HotkeyKey.D7 => "7",
        HotkeyKey.D8 => "8",
        HotkeyKey.D9 => "9",
        HotkeyKey.OemSemicolon => ";",
        HotkeyKey.OemPlus => "=",
        HotkeyKey.OemComma => ",",
        HotkeyKey.OemMinus => "-",
        HotkeyKey.OemPeriod => ".",
        HotkeyKey.OemQuestion => "/",
        HotkeyKey.OemTilde => "`",
        HotkeyKey.OemOpenBrackets => "[",
        HotkeyKey.OemPipe => "\\",
        HotkeyKey.OemCloseBrackets => "]",
        HotkeyKey.OemQuotes => "'",
        HotkeyKey.OemBackslash => "\\",
        _ => HotkeyGesture.KeyName(key),
    };

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System or Key.CapsLock or Key.NumLock or Key.Scroll;
}
