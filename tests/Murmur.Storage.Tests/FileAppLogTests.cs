using System.Runtime.CompilerServices;
using Murmur.Core.Diagnostics;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Storage.Tests;

public sealed class FileAppLogTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "murmur-fake-logs");

    private readonly ManualTimeProvider _time = new();
    private readonly FakeLogFileSystem _files;

    public FileAppLogTests()
    {
        _files = new FakeLogFileSystem(_time);
    }

    private FileAppLog Create(Func<string>? context = null) => new(Dir, _time, _files, context);

    private static string Day(DateTimeOffset at) => "murmur-" + at.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".log";

    private string AllText() => string.Concat(_files.Names(Dir).Select(name => _files.Text(Dir, name)));

    private int CountLines(string marker) =>
        AllText().Split('\n').Count(line => line.Contains(marker, StringComparison.Ordinal));

    [Fact]
    public void FormatLine_IsOneShortLine()
    {
        var at = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero).UtcTicks;

        Assert.Equal("2026-11-13T12:00:00Z WARN capture screen.fault device-lost\n", FileAppLog.FormatLine(at, LogLevel.Warning, "capture", "screen.fault", "device-lost"));
        Assert.Equal("2026-11-13T12:00:00Z ERROR app task.unobserved\n", FileAppLog.FormatLine(at, LogLevel.Error, "app", "task.unobserved", null));
        Assert.Equal("2026-11-13T12:00:00Z INFO a_b c two lines\n", FileAppLog.FormatLine(at, LogLevel.Info, "a b", "c", "two\nlines"));
    }

    [Fact]
    public void Ring_KeepsTheLatestEntriesInOrder_AndOverwritesTheOldest()
    {
        using var log = Create();
        for (var i = 0; i < FileAppLog.RingCapacity + 44; i++)
        {
            log.Write(LogLevel.Info, "test", "event" + i);
        }

        var events = log.Snapshot().Split('\n').Where(line => line.Contains(" INFO test ", StringComparison.Ordinal)).ToList();

        Assert.Equal(FileAppLog.RingCapacity, events.Count);
        Assert.EndsWith(" event44", events[0], StringComparison.Ordinal);
        Assert.EndsWith(" event" + (FileAppLog.RingCapacity + 43), events[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(events, line => line.EndsWith(" event43", StringComparison.Ordinal));
    }

    [Fact]
    public void InfoWrites_LeaveTheDiskUntouched_EvenAtExit()
    {
        using var log = Create();
        _files.ResetCounters();

        for (var i = 0; i < 10_000; i++)
        {
            log.Write(LogLevel.Info, "recording", "state", "recording");
            if (i % 1000 == 0)
            {
                _time.Advance(TimeSpan.FromMinutes(6));
            }
        }

        log.Flush();
        log.MarkCleanExit();

        Assert.Equal(0, _files.Operations);
        Assert.Empty(_files.Names(Dir));
    }

    [Fact]
    public void HealthySession_OnTheRealDisk_CreatesNoLogsFolder()
    {
        using var root = new TempDataRoot();
        var paths = new StoragePaths(root.Path);
        using (var log = new FileAppLog(paths, TimeProvider.System))
        {
            for (var i = 0; i < 1000; i++)
            {
                log.Write(LogLevel.Info, "recording", "tick");
            }

            log.Flush();
            log.MarkCleanExit();
        }

        Assert.False(Directory.Exists(paths.LogsDirectory));
    }

    [Fact]
    public void Write_OnTheCallersThread_NeverTouchesTheDisk()
    {
        using var log = Create();
        _files.ResetCounters();
        var caller = Environment.CurrentManagedThreadId;

        for (var i = 0; i < 100; i++)
        {
            log.Write(LogLevel.Warning, "capture", "screen.fault", "frames-dropped");
            log.Write(LogLevel.Error, "asr", "worker.exit", "code=1");
        }

        Assert.Equal(0, _files.Operations);

        // The batch goes out on the timer's thread (here a thread of its own that moves the clock).
        var timerThread = new Thread(() => _time.Advance(TimeSpan.Zero));
        timerThread.Start();
        timerThread.Join();

        Assert.Equal(1, log.Appends);
        Assert.NotEmpty(_files.Threads);
        Assert.DoesNotContain(caller, _files.Threads);
        Assert.Equal(200, CountLines(" asr worker.exit") + CountLines(" capture screen.fault"));
    }

    [Fact]
    public void Warnings_AreAppendedAtMostOncePer30Seconds_PlusOnceAtFlush()
    {
        using var log = Create();
        var total = 0;
        for (var second = 0; second < 300; second++)
        {
            for (var i = 0; i < 5; i++)
            {
                log.Write(LogLevel.Warning, "import", "file.failed", "a.wav");
                total++;
            }

            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var appendTimes = _files.AppendTimes;
        Assert.Equal(appendTimes.Count, log.Appends);
        Assert.InRange(appendTimes.Count, 1, 300 / 30 + 1);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), appendTimes[0]); // the first trouble goes out at once
        for (var i = 1; i < appendTimes.Count; i++)
        {
            Assert.True(appendTimes[i] - appendTimes[i - 1] >= FileAppLog.BatchInterval, $"appends {i - 1} and {i} are {appendTimes[i] - appendTimes[i - 1]} apart");
        }

        // What came after the last timed append goes out at exit.
        log.Write(LogLevel.Warning, "import", "file.failed", "a.wav");
        total++;
        var timed = log.Appends;
        log.Flush();

        Assert.Equal(timed + 1, log.Appends);
        Assert.Equal(total, CountLines(" WARN import file.failed a.wav"));

        // Nothing left: a second flush writes nothing.
        log.Flush();
        Assert.Equal(timed + 1, log.Appends);
    }

    [Fact]
    public void Crash_WritesTheExceptionTheRingAndTheVersions_AndFlushesThePendingWarnings()
    {
        using var log = Create(() => "App: Murmur 9.9\nSettings: theme=Dark");
        log.Write(LogLevel.Info, "shell", "page.library");
        log.Write(LogLevel.Warning, "hotkeys", "register.failed", "Record");
        var exception = Thrown(() => throw new InvalidOperationException("boom"));

        log.Crash(exception, "ui");

        var crash = Assert.Single(_files.Names(Dir), name => name.StartsWith("crash-", StringComparison.Ordinal));
        var text = _files.Text(Dir, crash)!;
        Assert.Contains("Area: ui", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException 0x", text, StringComparison.Ordinal);
        Assert.Contains("boom", text, StringComparison.Ordinal);
        Assert.Contains(nameof(Crash_WritesTheExceptionTheRingAndTheVersions_AndFlushesThePendingWarnings), text, StringComparison.Ordinal);
        Assert.Contains("Runtime: ", text, StringComparison.Ordinal);
        Assert.Contains("OS: ", text, StringComparison.Ordinal);
        Assert.Contains("App: Murmur 9.9", text, StringComparison.Ordinal);
        Assert.Contains("Settings: theme=Dark", text, StringComparison.Ordinal);
        Assert.Contains(" INFO shell page.library", text, StringComparison.Ordinal);
        Assert.Contains(" ERROR ui crash InvalidOperationException", text, StringComparison.Ordinal);

        // Synchronously, without the timer: the warning and the crash line are in the daily log too.
        var daily = _files.Text(Dir, Day(_time.GetUtcNow()))!;
        Assert.Contains(" WARN hotkeys register.failed Record", daily, StringComparison.Ordinal);
        Assert.Contains(" ERROR ui crash InvalidOperationException", daily, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", daily, StringComparison.Ordinal);
    }

    [Fact]
    public void Crash_ReportedByTwoHandlers_WritesOneFile()
    {
        using var log = Create();
        var exception = Thrown(() => throw new InvalidOperationException("once"));

        log.Crash(exception, "ui");
        log.Crash(exception, "app");
        log.Crash(Thrown(() => throw new InvalidOperationException("another")), "app");

        Assert.Equal(2, _files.Names(Dir).Count(name => name.StartsWith("crash-", StringComparison.Ordinal)));
    }

    [Fact]
    public void NextStart_ReportsTheUnseenCrash_UntilItIsDismissed()
    {
        string crashName;
        using (var first = Create())
        {
            Assert.Null(first.UnseenCrash);
            first.Crash(Thrown(() => throw new InvalidOperationException("x")), "app");
            crashName = _files.Names(Dir).Single(name => name.StartsWith("crash-", StringComparison.Ordinal));

            // The crash of this run is the next run's business.
            Assert.Null(first.UnseenCrash);
        }

        _time.Advance(TimeSpan.FromMinutes(1));
        using (var second = Create())
        {
            Assert.Equal(Path.Combine(Dir, crashName), second.UnseenCrash);
            second.MarkCrashSeen();
            Assert.Null(second.UnseenCrash);
        }

        using var third = Create();
        Assert.Null(third.UnseenCrash);
    }

    [Fact]
    public void CleanExit_AfterACrashedRun_HidesThatCrash_ButNotALaterOne()
    {
        using (var crashed = Create())
        {
            crashed.Crash(Thrown(() => throw new InvalidOperationException("first")), "app");
        }

        _time.Advance(TimeSpan.FromSeconds(5));
        using (var notDismissed = Create())
        {
            Assert.NotNull(notDismissed.UnseenCrash);
            notDismissed.MarkCleanExit();
        }

        _time.Advance(TimeSpan.FromSeconds(5));
        using (var clean = Create())
        {
            Assert.Null(clean.UnseenCrash);
            clean.Crash(Thrown(() => throw new InvalidOperationException("second")), "app");
        }

        _time.Advance(TimeSpan.FromSeconds(5));
        using var next = Create();
        var unseen = next.UnseenCrash;
        Assert.NotNull(unseen);
        Assert.Contains("second", _files.Text(Dir, Path.GetFileName(unseen))!, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanExit_WithoutAnyCrash_WritesNoMarker()
    {
        using var log = Create();

        log.MarkCleanExit();

        Assert.Empty(_files.Names(Dir));
    }

    [Fact]
    public void ADayFile_RotatesAt256KB_AndANewDayGetsItsOwnFile()
    {
        using var log = Create();
        var detail = new string('d', 190);
        for (var round = 0; round < 6; round++)
        {
            for (var i = 0; i < 250; i++)
            {
                log.Write(LogLevel.Warning, "import", "file.failed", detail);
            }

            log.Flush();
        }

        var today = Day(_time.GetUtcNow());
        var second = today.Replace(".log", ".2.log", StringComparison.Ordinal);
        Assert.Contains(today, _files.Names(Dir));
        Assert.Contains(second, _files.Names(Dir));
        Assert.All(_files.Names(Dir), name => Assert.InRange(_files.Length(Dir, name), 1, FileAppLog.MaxFileBytes));
        Assert.Equal(6 * 250, CountLines(" WARN import file.failed"));

        _time.Advance(TimeSpan.FromDays(1));
        log.Write(LogLevel.Warning, "import", "file.failed", "next day");
        log.Flush();

        Assert.Contains(" next day", _files.Text(Dir, Day(_time.GetUtcNow())), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFolder_StaysUnder2MB_DeletingTheOldestFirst()
    {
        var now = _time.GetUtcNow();
        var chunk = new string('x', 204 * 1024);
        for (var i = 1; i <= 10; i++)
        {
            _files.Seed(Dir, Day(now.AddDays(-i)), chunk, now.AddDays(-i));
        }

        using var log = Create();
        Assert.Equal(10, _files.Names(Dir).Count); // just under the cap: nothing to delete yet

        for (var i = 0; i < 200; i++)
        {
            log.Write(LogLevel.Error, "storage", "save.failed", "IOException 0x80070070");
        }

        log.Flush();

        var names = _files.Names(Dir);
        Assert.InRange(_files.TotalBytes(Dir), 1, FileAppLog.MaxFolderBytes);
        Assert.DoesNotContain(Day(now.AddDays(-10)), names);
        Assert.Contains(Day(now.AddDays(-9)), names);
        Assert.Contains(Day(now.AddDays(-1)), names);
        Assert.Contains(Day(now), names);
    }

    [Fact]
    public void Start_BringsAnOversizedFolderUnderTheCap()
    {
        var now = _time.GetUtcNow();
        var chunk = new string('x', 300 * 1024);
        for (var i = 1; i <= 8; i++)
        {
            _files.Seed(Dir, Day(now.AddDays(-i)), chunk, now.AddDays(-i));
        }

        using var log = Create();

        Assert.InRange(_files.TotalBytes(Dir), 1, FileAppLog.MaxFolderBytes);
        Assert.DoesNotContain(Day(now.AddDays(-8)), _files.Names(Dir));
        Assert.Contains(Day(now.AddDays(-1)), _files.Names(Dir));
    }

    [Fact]
    public void Start_DeletesWhatIsOlderThan14Days_AndOnlyOurFiles()
    {
        var now = _time.GetUtcNow();
        _files.Seed(Dir, Day(now.AddDays(-15)), "old\n", now.AddDays(-15));
        _files.Seed(Dir, "crash-20251215-000000-000.txt", "old crash", now.AddDays(-17));
        _files.Seed(Dir, Day(now.AddDays(-13)), "recent\n", now.AddDays(-13));
        _files.Seed(Dir, "notes.txt", "not ours", now.AddDays(-30));

        using var log = Create();

        Assert.Equal(new[] { Day(now.AddDays(-13)), "notes.txt" }.Order(StringComparer.Ordinal), _files.Names(Dir));
        Assert.Null(log.UnseenCrash);
    }

    [Fact]
    public void NoUserContent_ReachesTheDisk_ThroughAnyPublicApi()
    {
        const string title = "Quarterly budget with Olena Petrenko";
        const string transcript = "the launch date moves to the ninth of March";
        using var root = new TempDataRoot();
        var paths = new StoragePaths(root.Path);
        var sessionFile = Path.Combine(paths.RecordingsDirectory, title, "mic.wav");
        var log = new FileAppLog(paths, _time);

        log.Write(LogLevel.Warning, "storage", "save.failed", LogText.FileName(sessionFile));
        log.Write(LogLevel.Error, "ai", "summary.failed", LogText.Error(new InvalidOperationException(transcript + " in " + sessionFile)));
        log.Write(LogLevel.Warning, "storage", "recordings.fallback", LogText.Scrub($"Could not find a part of the path '{sessionFile}'."));
        log.Write(LogLevel.Error, "asr", "worker.exit", "code=1 | " + LogText.Tail($"loading {sessionFile}\nfailed on '{transcript}'"));
        _time.Advance(TimeSpan.FromSeconds(1));
        log.Crash(
            new IOException($"Could not open '{sessionFile}' for '{transcript}'", new FormatException($"The input string '{transcript}' was not in a correct format.")),
            "ui");
        log.Flush();
        log.MarkCleanExit();
        var snapshot = log.Snapshot();
        log.Dispose();

        // A later run that dismisses the crash and copies the diagnostics.
        _time.Advance(TimeSpan.FromMinutes(1));
        using var next = new FileAppLog(paths, _time);
        next.MarkCrashSeen();
        var nextSnapshot = next.Snapshot();

        var files = Directory.GetFiles(paths.LogsDirectory);
        Assert.Contains(files, file => Path.GetFileName(file).StartsWith("crash-", StringComparison.Ordinal));
        Assert.Contains(files, file => Path.GetFileName(file).StartsWith("murmur-", StringComparison.Ordinal));
        foreach (var text in files.Select(File.ReadAllText).Append(snapshot).Append(nextSnapshot))
        {
            Assert.DoesNotContain(title, text, StringComparison.Ordinal);
            Assert.DoesNotContain("Olena", text, StringComparison.Ordinal);
            Assert.DoesNotContain(transcript, text, StringComparison.Ordinal);
            Assert.DoesNotContain("launch date", text, StringComparison.Ordinal);
        }

        // What is useful stays.
        var all = string.Concat(files.Select(File.ReadAllText));
        Assert.Contains("mic.wav", all, StringComparison.Ordinal);
        Assert.Contains("IOException", all, StringComparison.Ordinal);
        Assert.Contains("FormatException", all, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentWriters_LoseNothing_ButWhatTheQueueCapCounts()
    {
        using var log = Create();
        const int writers = 8;
        const int perWriter = 4000;
        using var stop = new CancellationTokenSource();
        var clock = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                _time.Advance(TimeSpan.FromSeconds(3));
                Thread.Yield();
            }
        });
        clock.Start();

        var threads = Enumerable.Range(0, writers).Select(w => new Thread(() =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                log.Write(i % 2 == 0 ? LogLevel.Info : LogLevel.Warning, "w" + w, "e");
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        stop.Cancel();
        clock.Join();
        log.Flush();

        var text = AllText();
        var warnings = text.Split('\n').Count(line => line.Contains(" WARN w", StringComparison.Ordinal));
        var dropped = text.Split('\n')
            .Where(line => line.Contains(" WARN log dropped ", StringComparison.Ordinal))
            .Sum(line => int.Parse(line[(line.LastIndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(writers * perWriter / 2, warnings + dropped);

        var ring = log.Snapshot().Split('\n').Count(line => line.Contains(" w", StringComparison.Ordinal) && line.EndsWith(" e", StringComparison.Ordinal));
        Assert.Equal(FileAppLog.RingCapacity, ring);
    }

    [Fact]
    public void Write_OfAnInfoEvent_AllocatesNothing()
    {
        using var log = new FileAppLog(Dir, TimeProvider.System, _files);
        for (var i = 0; i < 1000; i++)
        {
            log.Write(LogLevel.Info, "bench", "write", "constant");
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++)
        {
            log.Write(LogLevel.Info, "bench", "write", "constant");
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void RealDisk_CrashFile_IsTheNextStartsUnseenCrash()
    {
        using var root = new TempDataRoot();
        var paths = new StoragePaths(root.Path);
        using (var log = new FileAppLog(paths, TimeProvider.System))
        {
            log.Crash(Thrown(() => throw new InvalidOperationException("disk")), "app");
        }

        using var next = new FileAppLog(paths, TimeProvider.System);

        Assert.NotNull(next.UnseenCrash);
        Assert.True(File.Exists(next.UnseenCrash));
        Assert.Equal(paths.LogsDirectory, next.Directory);
        Assert.Contains("disk", next.Snapshot(), StringComparison.Ordinal);
        next.MarkCrashSeen();
        Assert.Equal(Path.GetFileName(next.UnseenCrash ?? Directory.GetFiles(paths.LogsDirectory, "crash-*").Single()), File.ReadAllText(Path.Combine(paths.LogsDirectory, "last-exit")));
    }

    [Fact]
    public void Snapshot_HasTheRecentEventsAndTheLastCrash()
    {
        using var log = Create();
        log.Write(LogLevel.Warning, "calls", "monitor.read-failed", "COMException 0x88890004");

        Assert.Contains("No crash file.", log.Snapshot(), StringComparison.Ordinal);

        log.Crash(Thrown(() => throw new InvalidOperationException("snap")), "ui");
        var snapshot = log.Snapshot();

        Assert.Contains(" WARN calls monitor.read-failed COMException 0x88890004", snapshot, StringComparison.Ordinal);
        Assert.Contains("Last crash: crash-", snapshot, StringComparison.Ordinal);
        Assert.Contains("snap", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void ACrashFile_ListsAtMost8InnerExceptionsPerLevel_AndStaysUnderTheFileLimit()
    {
        // One subtree shared by every slot of the level above: 20 wide and 5 deep without millions of objects.
        Exception tree = new InvalidOperationException("leaf " + new string('x', 400));
        for (var depth = 0; depth < 5; depth++)
        {
            tree = new AggregateException(Enumerable.Repeat(tree, 20));
        }
        using var log = Create();
        for (var i = 0; i < FileAppLog.RingCapacity; i++)
        {
            log.Write(LogLevel.Warning, new string('a', 64), new string('c', 64), new string('d', 300));
        }

        log.Crash(new AggregateException(Enumerable.Repeat(tree, 30)), "app");

        var crash = _files.Names(Dir).Single(name => name.StartsWith("crash-", StringComparison.Ordinal));
        var text = _files.Text(Dir, crash)!;
        Assert.InRange(_files.Length(Dir, crash), 1, FileAppLog.MaxFileBytes);
        Assert.Contains("... 12 more inner exceptions", text, StringComparison.Ordinal); // 8 of the 20 at a level, depth first
        Assert.Contains("cut to the file size limit", text, StringComparison.Ordinal);
        Assert.Contains("Recent events (oldest first):", text, StringComparison.Ordinal); // the ring survives the cut
        Assert.Contains(" ERROR app crash AggregateException", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASmallCrash_IsNotCut()
    {
        using var log = Create();

        var text = log.BuildCrashText(new AggregateException(new InvalidOperationException("a"), new IOException("b")), "ui", _time.GetUtcNow());

        Assert.DoesNotContain("cut to the file size limit", text, StringComparison.Ordinal);
        Assert.DoesNotContain("more inner exceptions", text, StringComparison.Ordinal);
        Assert.Contains("IOException", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AWallClockSetBack_NeverHoldsTheBatchForLongerThanTheInterval()
    {
        var clock = new SettableClock(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        using var log = new FileAppLog(Dir, clock, _files);
        log.Write(LogLevel.Warning, "a", "first");
        Assert.Equal(TimeSpan.Zero, clock.Timer!.LastDue);
        clock.Timer.Fire();
        Assert.Equal(1, log.Appends);

        clock.UtcNow = clock.UtcNow.AddHours(-3);
        log.Write(LogLevel.Warning, "a", "after the jump");

        Assert.InRange(clock.Timer.LastDue, TimeSpan.Zero, FileAppLog.BatchInterval);
    }

    [Fact]
    public void ACrashAfterTheClockWasSetBack_IsStillUnseen()
    {
        var clock = new SettableClock(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var files = new FakeLogFileSystem(clock);
        using (var crashed = new FileAppLog(Dir, clock, files))
        {
            crashed.Crash(Thrown(() => throw new InvalidOperationException("first")), "app");
        }

        using (var seen = new FileAppLog(Dir, clock, files))
        {
            Assert.NotNull(seen.UnseenCrash);
            seen.MarkCrashSeen();
        }

        clock.UtcNow = clock.UtcNow.AddDays(-1);
        using (var again = new FileAppLog(Dir, clock, files))
        {
            Assert.Null(again.UnseenCrash);
            again.Crash(Thrown(() => throw new InvalidOperationException("after the jump")), "app");
            again.Crash(Thrown(() => throw new InvalidOperationException("same millisecond")), "app");
        }

        using var next = new FileAppLog(Dir, clock, files);
        var unseen = next.UnseenCrash;
        Assert.NotNull(unseen);
        Assert.Contains("same millisecond", files.Text(Dir, Path.GetFileName(unseen))!, StringComparison.Ordinal);
        Assert.Equal(3, files.Names(Dir).Count(name => name.StartsWith("crash-", StringComparison.Ordinal)));
    }

    /// <summary>A wall clock the test sets (also backwards); its one timer fires when the test says so.</summary>
    private sealed class SettableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = start;

        public ManualTimer? Timer { get; private set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timer = new ManualTimer(callback, state);

        public sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan LastDue { get; private set; } = Timeout.InfiniteTimeSpan;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                LastDue = dueTime;
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception Thrown(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Nothing was thrown.");
    }
}
