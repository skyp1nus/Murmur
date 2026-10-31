using System.Globalization;

namespace Murmur.Desktop;

/// <summary>
/// Work that would otherwise be paid for by the UI thread before the first frame, started on a pool thread at the top of
/// <c>Main</c> so it overlaps the Avalonia setup.
/// <para>
/// The first culture-aware case mapping of the process (<c>string.ToLower()</c>, as in Avalonia's <c>KeyGesture.Parse</c>, which
/// the Library's <c>KeyBinding Gesture="Enter"</c> reaches while the window is laid out) makes the runtime work out whether the
/// culture's ASCII casing matches the invariant one: it compares strings with ICU, which builds collators (tens of milliseconds of
/// CPU in a cold process). The result is cached per <see cref="TextInfo"/>, so doing the first mapping early, off the UI thread,
/// removes the cost from the start-up path. There is no reflection and nothing to dispose.
/// </para>
/// </summary>
internal static class StartupWarmUp
{
    /// <summary>Starts the warm-up on the thread pool and returns at once; the task completes when it is done (for tests).</summary>
    public static Task Begin() => Task.Run(Run);

    /// <summary>The warm-up itself: one case mapping for the current and for the invariant culture.</summary>
    internal static void Run()
    {
        _ = "Ab".ToLower(CultureInfo.CurrentCulture);
        _ = "Ab".ToUpper(CultureInfo.CurrentCulture);
        _ = "Ab".ToLowerInvariant();
        _ = "Ab".ToUpperInvariant();
    }
}
