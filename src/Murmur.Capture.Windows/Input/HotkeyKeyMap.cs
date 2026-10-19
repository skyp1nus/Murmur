using Murmur.Capture.Windows.Input.Interop;
using Murmur.Core.Input;

namespace Murmur.Capture.Windows.Input;

/// <summary>
/// <see cref="HotkeyGesture"/> -&gt; <c>RegisterHotKey</c> arguments. The virtual-key codes are the <c>VK_*</c> values of
/// winuser.h (Windows SDK 10.0.26100); pure, so it is unit-tested on every OS.
/// </summary>
internal static class HotkeyKeyMap
{
    /// <summary>The <c>VK_*</c> code of <paramref name="key"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="key"/> is not a defined <see cref="HotkeyKey"/>.</exception>
    public static ushort ToVirtualKey(HotkeyKey key) => key switch
    {
        >= HotkeyKey.A and <= HotkeyKey.Z => (ushort)('A' + (key - HotkeyKey.A)), // VK_A..VK_Z = 0x41..0x5A
        >= HotkeyKey.D0 and <= HotkeyKey.D9 => (ushort)('0' + (key - HotkeyKey.D0)), // VK_0..VK_9 = 0x30..0x39
        >= HotkeyKey.F1 and <= HotkeyKey.F24 => (ushort)(InputConstants.VkF1 + (key - HotkeyKey.F1)), // VK_F1..VK_F24 = 0x70..0x87
        >= HotkeyKey.NumPad0 and <= HotkeyKey.NumPad9 => (ushort)(InputConstants.VkNumpad0 + (key - HotkeyKey.NumPad0)),
        HotkeyKey.Space => InputConstants.VkSpace,
        HotkeyKey.Insert => InputConstants.VkInsert,
        HotkeyKey.Delete => InputConstants.VkDelete,
        HotkeyKey.Home => InputConstants.VkHome,
        HotkeyKey.End => InputConstants.VkEnd,
        HotkeyKey.PageUp => InputConstants.VkPrior,
        HotkeyKey.PageDown => InputConstants.VkNext,
        HotkeyKey.Up => InputConstants.VkUp,
        HotkeyKey.Down => InputConstants.VkDown,
        HotkeyKey.Left => InputConstants.VkLeft,
        HotkeyKey.Right => InputConstants.VkRight,
        HotkeyKey.Pause => InputConstants.VkPause,
        HotkeyKey.PrintScreen => InputConstants.VkSnapshot,
        HotkeyKey.OemSemicolon => InputConstants.VkOem1,
        HotkeyKey.OemPlus => InputConstants.VkOemPlus,
        HotkeyKey.OemComma => InputConstants.VkOemComma,
        HotkeyKey.OemMinus => InputConstants.VkOemMinus,
        HotkeyKey.OemPeriod => InputConstants.VkOemPeriod,
        HotkeyKey.OemQuestion => InputConstants.VkOem2,
        HotkeyKey.OemTilde => InputConstants.VkOem3,
        HotkeyKey.OemOpenBrackets => InputConstants.VkOem4,
        HotkeyKey.OemPipe => InputConstants.VkOem5,
        HotkeyKey.OemCloseBrackets => InputConstants.VkOem6,
        HotkeyKey.OemQuotes => InputConstants.VkOem7,
        HotkeyKey.Oem8 => InputConstants.VkOem8,
        HotkeyKey.OemBackslash => InputConstants.VkOem102,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Not a hotkey key."),
    };

    /// <summary>The <c>MOD_*</c> flags for <paramref name="modifiers"/>, plus <c>MOD_NOREPEAT</c> (holding the key fires once).</summary>
    public static uint ToNativeModifiers(HotkeyModifiers modifiers)
    {
        var native = InputConstants.ModNoRepeat;
        if ((modifiers & HotkeyModifiers.Ctrl) != 0)
        {
            native |= InputConstants.ModControl;
        }

        if ((modifiers & HotkeyModifiers.Alt) != 0)
        {
            native |= InputConstants.ModAlt;
        }

        if ((modifiers & HotkeyModifiers.Shift) != 0)
        {
            native |= InputConstants.ModShift;
        }

        if ((modifiers & HotkeyModifiers.Meta) != 0)
        {
            native |= InputConstants.ModWin;
        }

        return native;
    }
}
