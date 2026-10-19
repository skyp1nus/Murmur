namespace Murmur.Desktop.Tests;

public sealed class SingleInstanceTests
{
    /// <summary>The named mutex and events of one machine: the first channel opened is "first" until it is disposed.</summary>
    private sealed class FakeSystem
    {
        private readonly AutoResetEvent _signal = new(false);
        private readonly AutoResetEvent _ack = new(false);
        private int _firstHeld;

        public int Opened { get; private set; }

        public List<FakeChannel> Channels { get; } = [];

        public IInstanceChannel Open()
        {
            lock (Channels)
            {
                Opened++;
                var channel = new FakeChannel(this, _signal, _ack, isFirst: Interlocked.CompareExchange(ref _firstHeld, 1, 0) == 0);
                Channels.Add(channel);
                return channel;
            }
        }

        public void ReleaseFirst() => Interlocked.Exchange(ref _firstHeld, 0);
    }

    private sealed class FakeChannel(FakeSystem system, AutoResetEvent signal, AutoResetEvent ack, bool isFirst) : IInstanceChannel
    {
        private int _signals;

        public bool IsFirst { get; } = isFirst;

        public int Signals => Volatile.Read(ref _signals);

        public bool Disposed { get; private set; }

        public void SignalFirst()
        {
            Interlocked.Increment(ref _signals);
            ack.Reset();
            signal.Set();
        }

        public void Acknowledge() => ack.Set();

        public bool WaitAcknowledged(TimeSpan timeout) => ack.WaitOne(timeout);

        public bool Wait(CancellationToken cancellationToken) => WaitHandle.WaitAny([signal, cancellationToken.WaitHandle]) == 0;

        public void Dispose()
        {
            Disposed = true;
            if (IsFirst)
            {
                system.ReleaseFirst();
            }
        }
    }

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Short waits for the cases in which nobody answers.</summary>
    private static readonly SingleInstanceTiming Fast = new(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(500));

    /// <summary>A first instance whose app answers every request, as the real window does.</summary>
    private static SingleInstance EnterAnswering(FakeSystem system, Action? onRequest = null)
    {
        var first = SingleInstance.Enter([], system.Open);
        first.Requested += () =>
        {
            onRequest?.Invoke();
            first.Acknowledge();
        };
        return first;
    }

    [Fact]
    public void TheFirstStart_RunsTheApp()
    {
        var system = new FakeSystem();

        using var first = SingleInstance.Enter([], system.Open);

        Assert.True(first.IsPrimary);
        Assert.Equal(1, system.Opened);
    }

    [Fact]
    public void ASecondStart_ExitsOnlyOnceTheFirstHasShownItsWindow()
    {
        var system = new FakeSystem();
        using var woken = new ManualResetEventSlim();
        using var first = EnterAnswering(system, woken.Set);

        using var second = SingleInstance.Enter([], system.Open);

        Assert.False(second.IsPrimary);
        Assert.Equal(1, system.Channels[1].Signals);
        Assert.True(system.Channels[1].Disposed);
        Assert.True(woken.Wait(Patience, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ASecondStart_WhoseFirstNeverAnswers_RepeatsTheRequest_ThenGivesUpAfterTheRetryWindow()
    {
        var system = new FakeSystem();
        using var first = SingleInstance.Enter([], system.Open);
        var requests = 0;
        first.Requested += () => Interlocked.Increment(ref requests);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        using var second = SingleInstance.Enter([], system.Open, Fast);

        Assert.False(second.IsPrimary);
        Assert.True(clock.Elapsed >= Fast.RetryWindow);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        SpinWait.SpinUntil(() => Volatile.Read(ref requests) >= 2, Patience);
        Assert.True(Volatile.Read(ref requests) >= 2, "The request is repeated while waiting.");
    }

    [Fact]
    public async Task ASecondStart_TakesOver_WhenTheFirstInstanceEndsWhileItWaits()
    {
        var system = new FakeSystem();
        var first = SingleInstance.Enter([], system.Open);
        var slow = new SingleInstanceTiming(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(30));

        var secondStart = Task.Run(() => SingleInstance.Enter([], system.Open, slow), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(secondStart.IsCompleted, "It keeps waiting while the first one is alive and silent.");
        first.Dispose();

        using var second = await secondStart.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.True(second.IsPrimary);
    }

    [Fact]
    public async Task ASecondStart_AcceptsALateAnswer()
    {
        var system = new FakeSystem();
        using var first = SingleInstance.Enter([], system.Open);
        var slow = new SingleInstanceTiming(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(30));

        var secondStart = Task.Run(() => SingleInstance.Enter([], system.Open, slow), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        first.Acknowledge();

        using var second = await secondStart.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.False(second.IsPrimary);
    }

    [Fact]
    public void EachLaterStart_RaisesOneRequest()
    {
        var system = new FakeSystem();
        using var enough = new CountdownEvent(3);
        var threads = new List<bool>();
        using var first = EnterAnswering(system, () =>
        {
            lock (threads)
            {
                threads.Add(Thread.CurrentThread.IsThreadPoolThread);
            }

            enough.Signal();
        });

        for (var i = 0; i < 3; i++)
        {
            using var later = SingleInstance.Enter([], system.Open);
            Assert.False(later.IsPrimary);
        }

        Assert.True(enough.Wait(Patience, TestContext.Current.CancellationToken));
        Assert.All(threads, onPool => Assert.False(onPool));
    }

    [Theory]
    [InlineData("--startup-probe", "probe.json")]
    [InlineData("--autorecord", "60")]
    public void ADiagnosticRun_SkipsTheCheck_EvenWhenAnotherInstanceRuns(string flag, string value)
    {
        var system = new FakeSystem();
        var woken = 0;
        using var running = EnterAnswering(system, () => Interlocked.Increment(ref woken));

        using var diagnostic = SingleInstance.Enter([flag, value], system.Open);

        Assert.True(diagnostic.IsPrimary);
        Assert.Equal(1, system.Opened);
        Thread.Sleep(50);
        Assert.Equal(0, woken);
    }

    [Fact]
    public void IsDiagnostic_RecognisesOnlyTheDiagnosticSwitches()
    {
        Assert.True(SingleInstance.IsDiagnostic(["--startup-probe", "x.json"]));
        Assert.True(SingleInstance.IsDiagnostic(["--autorecord", "5", "--perf-out", "x"]));
        Assert.False(SingleInstance.IsDiagnostic([]));
        Assert.False(SingleInstance.IsDiagnostic(["--screen", "x"]));
    }

    [Fact]
    public void ANoChannel_OrAChannelThatCannotBeOpened_MeansNoCheck()
    {
        using var none = SingleInstance.Enter([], () => null);
        using var failing = SingleInstance.Enter([], () => throw new UnauthorizedAccessException("mutex owned by another user"));

        Assert.True(none.IsPrimary);
        Assert.True(failing.IsPrimary);
    }

    [Fact]
    public void Dispose_StopsTheListener_AndReleasesTheChannel()
    {
        var system = new FakeSystem();
        var first = SingleInstance.Enter([], system.Open);

        var started = System.Diagnostics.Stopwatch.StartNew();
        first.Dispose();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(system.Channels[0].Disposed);
    }

    [Fact]
    public void ARequestThatThrowsInAHandler_DoesNotStopTheListener()
    {
        var system = new FakeSystem();
        using var first = SingleInstance.Enter([], system.Open);
        using var answered = new ManualResetEventSlim();
        var calls = 0;
        first.Requested += () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("boom");
            }

            answered.Set();
            first.Acknowledge();
        };

        using (SingleInstance.Enter([], system.Open, Fast))
        {
        }

        SpinWait.SpinUntil(() => Volatile.Read(ref calls) >= 2, Patience);
        Assert.True(answered.Wait(Patience, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheNamesMatchTheInstaller()
    {
        Assert.Equal("MurmurSingleInstance", SingleInstance.MutexName);
        Assert.Equal("MurmurShowApp", SingleInstance.EventName);
        Assert.DoesNotContain("Global", SingleInstance.MutexName + SingleInstance.EventName + SingleInstance.AckEventName);
        var iss = File.ReadAllText(FindRepoFile("installer", "Murmur.iss"));
        Assert.Contains("AppMutex=" + SingleInstance.MutexName, iss);
    }

    private static string FindRepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(string.Join('/', parts));
    }
}
