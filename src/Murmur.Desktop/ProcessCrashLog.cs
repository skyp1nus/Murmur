using System.Globalization;
using System.Text;
using Murmur.Core.Diagnostics;
using Murmur.Core.Settings;
using Murmur.Storage;

namespace Murmur.Desktop;

/// <summary>The process's part of the crash log (#100): unhandled exceptions become crash files, unobserved task failures error lines.</summary>
internal static class ProcessCrashLog
{
    /// <summary>Hooks <see cref="AppDomain.UnhandledException"/> and <see cref="TaskScheduler.UnobservedTaskException"/> of this process.</summary>
    public static void Attach(IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => OnUnhandled(log, e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => OnUnobserved(log, e);
    }

    /// <summary>The process is going down: the crash file is written before it does.</summary>
    internal static void OnUnhandled(IAppLog log, object? exceptionObject) =>
        log.Crash(exceptionObject as Exception ?? new NonExceptionThrown(exceptionObject), "app");

    /// <summary>A faulted task nobody awaited: one error line, and the failure counts as observed.</summary>
    internal static void OnUnobserved(IAppLog log, UnobservedTaskExceptionEventArgs e)
    {
        log.Write(LogLevel.Error, "app", "task.unobserved", LogText.Error(e.Exception));
        e.SetObserved();
    }

    /// <summary>
    /// The process ended normally. Only the user's own app run counts as a clean exit that marks an earlier crash as seen; a second start
    /// that only woke the first one and a diagnostic run (<see cref="SingleInstance.IsDiagnostic"/>: the probes and <c>--autorecord</c>,
    /// which the scripts run against the real data folder) just flush their warnings, so the notice of a real crash stays for the user.
    /// </summary>
    internal static void OnExit(FileAppLog log, IReadOnlyList<string> args, bool isPrimary)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(args);
        if (isPrimary && !SingleInstance.IsDiagnostic(args))
        {
            log.MarkCleanExit();
        }
        else
        {
            log.Flush();
        }
    }

    /// <summary>Something that is not an <see cref="Exception"/> was thrown (another language's code); only its type is kept.</summary>
    internal sealed class NonExceptionThrown(object? thrown)
        : Exception("A non-exception object was thrown: " + (thrown?.GetType().Name ?? "null") + ".");
}

/// <summary>
/// The lines a crash file and "Copy diagnostics" add after the OS and runtime (#100): the app version and the main settings flags. No
/// paths, names or other user content: only whether a custom recordings folder is set, never which.
/// </summary>
internal sealed class DiagnosticContext
{
    /// <summary>The settings to describe; set by the composition root once they are loaded.</summary>
    public ISettingsStore? Settings { get; set; }

    public string Describe()
    {
        var text = new StringBuilder();
        text.Append("App: Murmur ").Append(Murmur.App.BuildInfo.Version).Append('\n');
        if (Settings?.Current is { } s)
        {
            text.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"Settings: language={s.Language} theme={s.Theme} acceleration={s.Acceleration} onboarding={s.OnboardingCompleted} screenByDefault={s.ScreenByDefault} detectCalls={s.DetectCalls} followAlong={s.FollowAlong} customFolder={!string.IsNullOrWhiteSpace(s.RecordingsDirectory)}"));
            text.Append('\n');
        }

        return text.ToString();
    }
}
