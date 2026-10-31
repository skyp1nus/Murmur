using Avalonia.Controls;
using Avalonia.Threading;

namespace Murmur.App.Services;

/// <summary>
/// Runs start-up work that the first frame does not need after that frame has been drawn. Work on the UI thread between the
/// window opening and the first frame (loading the library, building its rows) delays the frame, and the start-up budget is
/// "process start to first rendered frame".
/// </summary>
internal static class FirstFrame
{
    /// <summary>If no frame is ever requested (the window is hidden or minimized at start), the work runs after this long anyway.</summary>
    internal static readonly TimeSpan Fallback = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs <paramref name="work"/> once on the UI thread: after <paramref name="window"/> has opened and two animation frames
    /// have been requested (the first is rendered by the time the second callback runs), or after <see cref="Fallback"/>,
    /// whichever comes first. Never runs synchronously from <see cref="Window.Opened"/>.
    /// </summary>
    public static void Run(Window window, Action work)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(work);

        var done = false;
        IDisposable? fallback = null;
        window.Opened += (_, _) =>
        {
            fallback = DispatcherTimer.RunOnce(Once, Fallback);
            window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => Once()));
        };

        void Once()
        {
            if (done)
            {
                return;
            }

            done = true;
            fallback?.Dispose();
            work();
        }
    }
}
