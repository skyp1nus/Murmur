using Murmur.Core.Settings;

namespace Murmur.Core.Intelligence;

/// <summary>Why the chat server runs on a different build than the one the user chose.</summary>
public enum AccelerationFallback
{
    /// <summary>The chosen build runs (or the CPU build was chosen).</summary>
    None,

    /// <summary>The chosen GPU build is not installed: the caller offers the download; the CPU build serves meanwhile.</summary>
    RuntimeNotInstalled,

    /// <summary>The GPU server failed to start or died while loading (driver missing, out of VRAM, ...): the CPU build serves for this session.</summary>
    StartFailed,
}

/// <summary>Which build the chat server uses and, when it is not the chosen one, why.</summary>
/// <param name="Requested">The build the user chose (<see cref="AppSettings.Acceleration"/>).</param>
/// <param name="Active">The build the chat server runs on (or would run on at its next start).</param>
/// <param name="Fallback">Why <paramref name="Active"/> differs from <paramref name="Requested"/>.</param>
/// <param name="Detail">For <see cref="AccelerationFallback.StartFailed"/>: the last line the server printed, in English, for "GPU unavailable: {Detail}".</param>
public sealed record AccelerationStatus(AiAcceleration Requested, AiAcceleration Active, AccelerationFallback Fallback, string? Detail = null)
{
    /// <summary>The chosen GPU build has to be downloaded first (nothing downloads by itself).</summary>
    public bool NeedsDownload => Fallback == AccelerationFallback.RuntimeNotInstalled;
}

/// <summary>Where the Settings control reads which AI build runs and why a GPU build does not.</summary>
public interface IAccelerationStatusSource
{
    /// <summary>The current state; cheap, never blocks on a model load.</summary>
    AccelerationStatus Current { get; }

    /// <summary>
    /// Raised when <see cref="Current"/> changes (a GPU server failed to start, another build was chosen, a runtime was
    /// installed or removed). Raised off the UI thread: subscribers marshal.
    /// </summary>
    event Action<AccelerationStatus>? Changed;
}
