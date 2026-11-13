namespace Murmur.Core.Diagnostics;

/// <summary>How serious an event of the <see cref="IAppLog"/> is: only warnings and errors ever reach the disk.</summary>
public enum LogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// The app's diagnostic log. Events go to a fixed in-memory ring; the disk is written only for warnings and errors
/// (batched, at most one append per 30 s plus one at exit) and for crashes (synchronously). Never throws, never blocks
/// on I/O on the caller's thread. Not for audio/video callbacks: they report through their existing fault events.
/// No user content: callers pass codes and short details, paths reduced to file names.
/// </summary>
public interface IAppLog
{
    void Write(LogLevel level, string area, string code, string? detail = null);

    /// <summary>Writes crash-&lt;time&gt;.txt (the exception, the ring, versions) synchronously and flushes pending warnings.</summary>
    void Crash(Exception exception, string area);

    /// <summary>Flushes pending warnings (at exit).</summary>
    void Flush();

    /// <summary>The crash file of the previous run if it ended in a crash and the user has not dismissed it, else null.</summary>
    string? UnseenCrash { get; }

    void MarkCrashSeen();

    string Directory { get; }

    /// <summary>The recent events and the last crash as plain text, for "Copy diagnostics".</summary>
    string Snapshot();
}

/// <summary>The log of designers, tests and the parity boards: keeps nothing, writes nothing, never reports a crash.</summary>
public sealed class NullAppLog : IAppLog
{
    /// <summary>The shared instance (the type has no state).</summary>
    public static NullAppLog Instance { get; } = new();

    public string? UnseenCrash => null;

    public string Directory => "";

    public void Write(LogLevel level, string area, string code, string? detail = null)
    {
    }

    public void Crash(Exception exception, string area)
    {
    }

    public void Flush()
    {
    }

    public void MarkCrashSeen()
    {
    }

    public string Snapshot() => "";
}
