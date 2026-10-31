namespace Murmur.Core.Platform;

/// <summary>
/// The system's "reduce motion" preference (Windows: Settings → Accessibility → Visual effects → Animation effects off).
/// Decorative animation (orbs breathing or pulsing, halos) holds still while it is on; anything that carries information
/// (level meters, progress) keeps updating.
/// </summary>
public interface IMotionPreference
{
    /// <summary>True while the user asks the system for less motion; cheap, never blocks.</summary>
    bool ReduceMotion { get; }

    /// <summary>Raised when <see cref="ReduceMotion"/> changes. Raised off the UI thread: subscribers marshal.</summary>
    event Action? Changed;
}

/// <summary><see cref="IMotionPreference"/> for platforms without an implementation (and for headless runs): motion is never reduced.</summary>
public sealed class FullMotionPreference : IMotionPreference
{
    public bool ReduceMotion => false;

    public event Action? Changed
    {
        add { }
        remove { }
    }
}
