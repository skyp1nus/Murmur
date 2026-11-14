using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Storage.Tests;

/// <summary>An unreachable network share must not delay the start of a recording: the wait is bounded and the default folder is used.</summary>
public sealed class RecordingsFolderTimeoutTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 14, 9, 26, 53, TimeSpan.Zero);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);
    private static readonly string Share = Path.Combine(Path.GetTempPath(), "murmur-fake-share", "meetings");

    private readonly TempDataRoot _root = new();
    private readonly TempDataRoot _outside = new();
    private readonly ManualTimeProvider _time = new();
    private readonly ManualResetEventSlim _release = new();
    private readonly SemaphoreSlim _entered = new(0);
    private readonly List<SqliteStorage> _opened = [];
    private int _calls;
    private int _finished;
    private string? _configured;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _release.Set();
        foreach (var storage in _opened)
        {
            storage.Dispose();
        }

        _root.Dispose();
        _outside.Dispose();
        _release.Dispose();
        _entered.Dispose();
    }

    private SqliteStorage Open(Func<string, string> prepare, TimeProvider? time = null, TimeSpan? timeout = null)
    {
        var probe = new RecordingsFolderProbe(prepare, time ?? _time, timeout ?? Bound);
        var storage = new SqliteStorage(new StoragePaths(_root.Path), () => _configured, probe);
        _opened.Add(storage);
        return storage;
    }

    /// <summary>A probe that never answers until the test releases it, like a hung SMB call.</summary>
    private string Blocked(string folder)
    {
        Interlocked.Increment(ref _calls);
        _entered.Release();
        try
        {
            _release.Wait();
            throw new IOException("The network path was not found.");
        }
        finally
        {
            Interlocked.Increment(ref _finished);
        }
    }

    private string Reachable(string folder)
    {
        Interlocked.Increment(ref _calls);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private async Task WaitForTimerAsync()
    {
        while (_time.ActiveTimers == 0)
        {
            await Task.Delay(5, Ct);
        }
    }

    [Fact]
    public async Task BlockingProbe_FallsBackWhenTheTimeoutPasses_AndReportsOnce()
    {
        _configured = Share;
        var storage = Open(Blocked);
        var reported = new List<RecordingsFolderFallbackInfo>();
        storage.RecordingsFolderFallback += reported.Add;

        var creating = storage.CreateSessionAsync("x", T0, Ct).AsTask();
        await _entered.WaitAsync(Ct);
        await WaitForTimerAsync();
        Assert.False(creating.IsCompleted);

        _time.Advance(Bound);
        var session = await creating.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(Path.Combine(_root.Path, "Recordings"), Path.GetDirectoryName(storage.GetFiles(session.Id).Directory));
        var info = Assert.Single(reported);
        Assert.Equal(session.Id, info.Session);
        Assert.Equal(Share, info.RequestedFolder);
        Assert.Equal(RecordingsFolderProbe.TimedOutReason, info.Reason);
        Assert.DoesNotContain("meetings", info.Reason, StringComparison.Ordinal);
        Assert.Same(info, storage.LastFallback);
    }

    [Fact]
    public async Task BlockingProbe_WithARealShortTimeout_FallsBackQuickly()
    {
        _configured = Share;
        var storage = Open(Blocked, TimeProvider.System, TimeSpan.FromMilliseconds(100));

        var started = System.Diagnostics.Stopwatch.StartNew();
        var session = await storage.CreateSessionAsync("x", T0, Ct);
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.Elapsed}");
        Assert.NotNull(storage.LastFallback);
        Assert.Equal(Path.Combine(_root.Path, "Recordings"), Path.GetDirectoryName(storage.GetFiles(session.Id).Directory));
    }

    [Fact]
    public async Task ReachableFolder_IsUsed_WithoutFallback()
    {
        _configured = Path.Combine(_outside.Path, "ok");
        var storage = Open(Reachable);
        var reported = new List<RecordingsFolderFallbackInfo>();
        storage.RecordingsFolderFallback += reported.Add;

        var session = await storage.CreateSessionAsync("x", T0, Ct);

        Assert.Equal(_configured, Path.GetDirectoryName(storage.GetFiles(session.Id).Directory));
        Assert.True(Directory.Exists(storage.GetFiles(session.Id).Directory));
        Assert.Null(storage.LastFallback);
        Assert.Empty(reported);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task FailingProbe_FallsBack_WithItsMessage_AndReportsOnce()
    {
        _configured = Share;
        var storage = Open(_ => throw new IOException("The network name cannot be found."));
        var reported = new List<RecordingsFolderFallbackInfo>();
        storage.RecordingsFolderFallback += reported.Add;

        var session = await storage.CreateSessionAsync("x", T0, Ct);

        Assert.Equal(Path.Combine(_root.Path, "Recordings"), Path.GetDirectoryName(storage.GetFiles(session.Id).Directory));
        var info = Assert.Single(reported);
        Assert.Equal("The network name cannot be found.", info.Reason);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public async Task EachRecording_ProbesTheFolderAgain_BecauseTheShareMayBeBack()
    {
        _configured = Path.Combine(_outside.Path, "ok");
        var storage = Open(Reachable);

        await storage.CreateSessionAsync("a", T0, Ct);
        await storage.CreateSessionAsync("b", T0.AddMinutes(1), Ct);

        Assert.Equal(2, _calls);
    }

    [Fact]
    public async Task StuckProbe_IsNotRestartedByTheNextRecording_ButIsAfterItEnds()
    {
        _configured = Share;
        var storage = Open(Blocked, TimeProvider.System, TimeSpan.FromMilliseconds(100));

        await storage.CreateSessionAsync("a", T0, Ct);
        await storage.CreateSessionAsync("b", T0.AddMinutes(1), Ct);
        Assert.Equal(1, _calls);
        Assert.NotNull(storage.LastFallback);

        _release.Set();
        await WaitUntilAsync(() => Volatile.Read(ref _finished) == 1);
        _release.Reset();
        await storage.CreateSessionAsync("c", T0.AddMinutes(2), Ct);

        Assert.Equal(2, _calls);
    }

    [Fact]
    public async Task AbandonedProbeThatFailsLater_RaisesNoUnobservedException()
    {
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            _configured = Share;
            var storage = Open(Blocked, TimeProvider.System, TimeSpan.FromMilliseconds(50));
            await storage.CreateSessionAsync("x", T0, Ct);

            _release.Set();
            await Task.Delay(200, Ct);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.Equal(0, unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10, Ct);
        }
    }
}
