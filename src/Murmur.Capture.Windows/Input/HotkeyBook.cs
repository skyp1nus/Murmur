using Murmur.Core.Input;

namespace Murmur.Capture.Windows.Input;

/// <summary>
/// Which gesture each action currently holds, and the <c>RegisterHotKey</c> id of each action (one id per action).
/// Windows itself does not refuse the same combination under two ids of one thread, so the duplicate check is ours.
/// Not thread-safe: owned by the hotkey thread. Pure, so it is unit-tested on every OS.
/// </summary>
internal sealed class HotkeyBook
{
    private static readonly int ActionCount = Enum.GetValues<HotkeyAction>().Length;

    private readonly HotkeyGesture?[] _held = new HotkeyGesture?[ActionCount];

    /// <summary>The <c>RegisterHotKey</c> id of <paramref name="action"/> (1-based, unique per action).</summary>
    public static int IdOf(HotkeyAction action) => (int)action + 1;

    /// <summary>The action behind a <c>WM_HOTKEY</c> id, false for an id this book never hands out.</summary>
    public static bool TryActionOf(nint id, out HotkeyAction action)
    {
        if (id >= 1 && id <= ActionCount)
        {
            action = (HotkeyAction)(id - 1);
            return true;
        }

        action = default;
        return false;
    }

    public bool TryGet(HotkeyAction action, out HotkeyGesture gesture)
    {
        if (_held[(int)action] is { } held)
        {
            gesture = held;
            return true;
        }

        gesture = default;
        return false;
    }

    /// <summary>True when an action other than <paramref name="action"/> holds <paramref name="gesture"/>.</summary>
    public bool IsHeldByOther(HotkeyAction action, HotkeyGesture gesture)
    {
        for (var i = 0; i < _held.Length; i++)
        {
            if (i != (int)action && _held[i] == gesture)
            {
                return true;
            }
        }

        return false;
    }

    public void Set(HotkeyAction action, HotkeyGesture gesture) => _held[(int)action] = gesture;

    public void Remove(HotkeyAction action) => _held[(int)action] = null;

    /// <summary>The actions that hold a gesture now.</summary>
    public IEnumerable<HotkeyAction> HeldActions()
    {
        for (var i = 0; i < _held.Length; i++)
        {
            if (_held[i] is not null)
            {
                yield return (HotkeyAction)i;
            }
        }
    }
}
