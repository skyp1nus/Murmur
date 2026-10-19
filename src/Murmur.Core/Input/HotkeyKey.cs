namespace Murmur.Core.Input;

/// <summary>
/// The keys a global hotkey may use (together with at least one of Ctrl/Alt/Meta, except F13..F24, see
/// <see cref="HotkeyGesture"/>). Platform-neutral: each <c>Murmur.Capture.*</c> implementation maps them to its own key
/// codes. The text of a key in a gesture is the member name, except for the two punctuation keys that Avalonia prints by
/// their OEM number (<see cref="OemTilde"/> = <c>Oem3</c>, <see cref="OemOpenBrackets"/> = <c>Oem4</c>).
/// </summary>
public enum HotkeyKey
{
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    /// <summary>The digit row key 0 (Avalonia <c>Key.D0</c>).</summary>
    D0,
    D1, D2, D3, D4, D5, D6, D7, D8, D9,

    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,

    Space,
    Insert,
    Delete,
    Home,
    End,
    PageUp,
    PageDown,
    Up,
    Down,
    Left,
    Right,

    NumPad0, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,

    Pause,
    PrintScreen,

    /// <summary><c>;</c> on a US keyboard (Avalonia <c>OemSemicolon</c>).</summary>
    OemSemicolon,

    /// <summary><c>=</c>/<c>+</c> (<c>OemPlus</c>).</summary>
    OemPlus,

    /// <summary><c>,</c> (<c>OemComma</c>).</summary>
    OemComma,

    /// <summary><c>-</c> (<c>OemMinus</c>).</summary>
    OemMinus,

    /// <summary><c>.</c> (<c>OemPeriod</c>).</summary>
    OemPeriod,

    /// <summary><c>/</c> on a US keyboard (<c>OemQuestion</c>).</summary>
    OemQuestion,

    /// <summary>The backtick/tilde key; text <c>Oem3</c> (Avalonia prints it that way).</summary>
    OemTilde,

    /// <summary><c>[</c> on a US keyboard; text <c>Oem4</c> (Avalonia prints it that way).</summary>
    OemOpenBrackets,

    /// <summary><c>\</c> on a US keyboard (<c>OemPipe</c>).</summary>
    OemPipe,

    /// <summary><c>]</c> on a US keyboard (<c>OemCloseBrackets</c>).</summary>
    OemCloseBrackets,

    /// <summary><c>'</c> on a US keyboard (<c>OemQuotes</c>).</summary>
    OemQuotes,

    /// <summary>Layout-specific OEM key (<c>Oem8</c>).</summary>
    Oem8,

    /// <summary>The extra key of 102-key keyboards next to left Shift (<c>OemBackslash</c>).</summary>
    OemBackslash,
}
