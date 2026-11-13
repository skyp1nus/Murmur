using Murmur.Core.Diagnostics;

namespace Murmur.Core.Tests.Fakes;

/// <summary>An <see cref="IAppLog"/> that keeps what it was told, for asserting which failures a component reports.</summary>
internal sealed class RecordingAppLog : IAppLog
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, string Area, string Code, string? Detail)> _events = [];
    private readonly List<(Exception Exception, string Area)> _crashes = [];

    public RecordingAppLog(string directory = "", string? unseenCrash = null)
    {
        Directory = directory;
        UnseenCrash = unseenCrash;
    }

    public IReadOnlyList<(LogLevel Level, string Area, string Code, string? Detail)> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<(Exception Exception, string Area)> Crashes
    {
        get
        {
            lock (_gate)
            {
                return [.. _crashes];
            }
        }
    }

    public int Flushes { get; private set; }

    public int CrashSeenCalls { get; private set; }

    public string SnapshotText { get; set; } = "snapshot";

    public string? UnseenCrash { get; private set; }

    public string Directory { get; }

    public void Write(LogLevel level, string area, string code, string? detail = null)
    {
        lock (_gate)
        {
            _events.Add((level, area, code, detail));
        }
    }

    public void Crash(Exception exception, string area)
    {
        lock (_gate)
        {
            _crashes.Add((exception, area));
        }
    }

    public void Flush() => Flushes++;

    public void MarkCrashSeen()
    {
        CrashSeenCalls++;
        UnseenCrash = null;
    }

    public string Snapshot() => SnapshotText;
}
