using Avalonia.Controls;
using Avalonia.Threading;
using Murmur.Core.Diagnostics;

namespace Murmur.App.Diagnostics;

/// <summary>
/// The hidden <c>--probe-crash</c> flag (#100): once the main window has opened, the UI thread throws <see cref="CrashProbeException"/>, so the
/// whole crash path runs for real (the dispatcher's handler, the crash file, the process's end) and the next start shows the Library notice.
/// </summary>
public static class CrashProbe
{
    public const string Flag = "--probe-crash";

    public static bool IsRequested(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (var arg in args)
        {
            if (string.Equals(arg, Flag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Throws on the UI thread right after <paramref name="window"/> opened.</summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Opened += (_, _) => Dispatcher.UIThread.Post(static () => throw new CrashProbeException());
    }
}

/// <summary>The exception <see cref="CrashProbe"/> throws.</summary>
public sealed class CrashProbeException() : Exception("Forced by " + CrashProbe.Flag + ".");

/// <summary>The UI thread's part of the crash log: an exception nobody handled on the dispatcher becomes a crash file.</summary>
public static class UiCrashLog
{
    /// <summary>
    /// Records every unhandled exception of <paramref name="dispatcher"/> with <see cref="IAppLog.Crash"/> and leaves it unhandled, so the app
    /// still ends as before (the process's own handler then sees the same exception, which the log writes once).
    /// </summary>
    public static void Attach(Dispatcher dispatcher, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(log);
        dispatcher.UnhandledException += (_, e) => log.Crash(e.Exception, "ui");
    }
}
