namespace Murmur.Core.Platform;

/// <summary>
/// Platform styling of a small floating window such as the recorder orb (#46), beyond what Avalonia window properties can do.
/// Implemented where the platform has the means (Windows); the app works without it.
/// </summary>
public interface IFloatingWindowStyle
{
    /// <summary>Makes a top-level window non-activating (clicks never take focus) and keeps it out of Alt+Tab.</summary>
    /// <param name="nativeHandle">The native window handle (Windows: the HWND) of a window that has been created, before it is first shown.</param>
    void MakeNonActivating(nint nativeHandle);
}
