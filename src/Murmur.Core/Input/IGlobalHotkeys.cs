namespace Murmur.Core.Input;

/// <summary>What a global hotkey does; each action has at most one gesture.</summary>
public enum HotkeyAction
{
    /// <summary>Start a recording, or stop the running one (<c>AppSettings.RecordHotkey</c>).</summary>
    ToggleRecording,

    /// <summary>Turn screen recording on or off during a recording (<c>AppSettings.ToggleScreenHotkey</c>).</summary>
    ToggleScreen,

    /// <summary>Bring the main window to the front (<c>AppSettings.ShowAppHotkey</c>).</summary>
    ShowApp,
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,

    /// <summary>The Windows key (Command on macOS).</summary>
    Meta = 8,
}

public enum HotkeyRegistration
{
    Registered,

    /// <summary>The gesture was <c>null</c>: the action has no hotkey now.</summary>
    Cleared,

    /// <summary>Not a gesture this platform can register globally (see <c>HotkeyGesture.TryParse</c>).</summary>
    Invalid,

    /// <summary>Another application (or another action of this app) already holds the gesture.</summary>
    Taken,

    /// <summary>Global hotkeys are not available on this platform.</summary>
    Unsupported,
}

/// <summary>
/// System-wide hotkeys that work while the app is in the background. One implementation per OS lives in
/// <c>Murmur.Capture.*</c>. Gestures use the settings syntax, parsed by <c>HotkeyGesture</c>. Thread-safe;
/// <see cref="Pressed"/> is raised off the UI thread.
/// </summary>
public interface IGlobalHotkeys : IDisposable
{
    bool IsSupported { get; }

    /// <summary>
    /// Registers <paramref name="gesture"/> for <paramref name="action"/>, replacing that action's previous gesture.
    /// When the new gesture cannot be registered the action keeps no hotkey (the previous one is released either way),
    /// so the result always tells the truth about what works now.
    /// </summary>
    HotkeyRegistration Register(HotkeyAction action, string? gesture);

    /// <summary>The gesture of <paramref name="action"/> was pressed. Raised once per press (key repeat is ignored).</summary>
    event Action<HotkeyAction>? Pressed;
}
