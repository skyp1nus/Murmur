namespace Murmur.App.Controls;

/// <summary>Anything that can tell the orb how loud the audio is right now. Polled by the orb's render tick.</summary>
public interface ILevelSource
{
    /// <summary>Peak level in [0, 1]. Must be cheap and lock-free: it is read once per frame.</summary>
    float Level { get; }
}
