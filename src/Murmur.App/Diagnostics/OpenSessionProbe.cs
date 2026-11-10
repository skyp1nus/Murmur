using Murmur.App.ViewModels;

namespace Murmur.App.Diagnostics;

/// <summary>
/// Smoke test of the recording details used by CI (#102): with <c>--probe-open-latest</c> the app loads the library, opens the
/// newest session's details, lets the page render two frames and exits with <see cref="Opened"/>. The Native AOT build once
/// overflowed the stack on exactly this path while the JIT build did not, so CI runs it against the published binary.
/// Without the flag nothing changes.
/// </summary>
public static class OpenSessionProbe
{
    public const string Flag = "--probe-open-latest";

    /// <summary>The details opened, loaded and rendered.</summary>
    public const int Opened = 0;

    /// <summary>Opening or loading the details failed (the message goes to stderr).</summary>
    public const int Failed = 1;

    /// <summary>The library has no session to open.</summary>
    public const int NoSession = 3;

    /// <summary><see langword="true"/> when <paramref name="args"/> contain <see cref="Flag"/>.</summary>
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

    /// <summary>
    /// Loads the library of <paramref name="shell"/>, opens its first (newest) row, waits for the session to load and for two
    /// frames from <paramref name="nextFrame"/> (the page has been measured, arranged and rendered by the second one).
    /// </summary>
    public static async Task<int> RunAsync(ShellViewModel shell, Func<Task> nextFrame, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(nextFrame);
        ArgumentNullException.ThrowIfNull(log);

        await shell.InitializeAsync().ConfigureAwait(true);
        if (shell.Library.Sessions is not [var newest, ..])
        {
            await log.WriteLineAsync($"{Flag}: the library has no session to open.").ConfigureAwait(true);
            return NoSession;
        }

        shell.OpenSession(newest.Record);
        await shell.SessionLoadTask.ConfigureAwait(true);
        await nextFrame().ConfigureAwait(true);
        await nextFrame().ConfigureAwait(true);
        if (shell.CurrentPage is not SessionViewModel session || session.Id != newest.Record.Id)
        {
            await log.WriteLineAsync($"{Flag}: the details of {newest.Record.Id} did not stay open.").ConfigureAwait(true);
            return Failed;
        }

        await log.WriteLineAsync($"{Flag}: opened {newest.Record.Id}.").ConfigureAwait(true);
        return Opened;
    }
}
