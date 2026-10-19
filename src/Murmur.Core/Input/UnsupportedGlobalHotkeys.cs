namespace Murmur.Core.Input;

/// <summary>
/// <see cref="IGlobalHotkeys"/> for platforms without an implementation (and for headless runs): nothing is ever
/// registered, <see cref="Pressed"/> never fires.
/// </summary>
public sealed class UnsupportedGlobalHotkeys : IGlobalHotkeys
{
    public bool IsSupported => false;

    public event Action<HotkeyAction>? Pressed
    {
        add { }
        remove { }
    }

    /// <summary><see cref="HotkeyRegistration.Cleared"/> for <c>null</c> (the action has no hotkey, as asked), else <see cref="HotkeyRegistration.Unsupported"/>.</summary>
    public HotkeyRegistration Register(HotkeyAction action, string? gesture) =>
        gesture is null ? HotkeyRegistration.Cleared : HotkeyRegistration.Unsupported;

    public void Dispose()
    {
    }
}
