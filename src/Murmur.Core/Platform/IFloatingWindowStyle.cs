namespace Murmur.Core.Platform;

/// <summary>
/// Platform styling of a small floating window such as the recorder orb (#46), beyond what Avalonia window properties can do.
/// Implemented where the platform has the means (Windows); the app works without it.
/// </summary>
public interface IFloatingWindowStyle
{
    /// <summary>
    /// Makes a top-level window non-activating (clicks never take focus) and keeps it out of Alt+Tab, and keeps it so for the window's
    /// lifetime, also when the UI framework rewrites the window's style later (on show, a topmost change, a resize). Idempotent: call it
    /// once the window exists and again after it is shown.
    /// </summary>
    /// <param name="nativeHandle">The native window handle (Windows: the HWND) of a window of this process.</param>
    void MakeNonActivating(nint nativeHandle);
}
