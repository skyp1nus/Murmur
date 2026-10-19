namespace Murmur.Desktop.Tests;

public sealed class SingleInstanceTests
{
    /// <summary>The named mutex and event of one machine: the first channel opened is "first", later ones are not.</summary>
    private sealed class FakeSystem
    {
        private readonly AutoResetEvent _signal = new(false);
        private bool _created;

        public int Opened { get; private set; }

        public List<FakeChannel> Channels { get; } = [];

        public IInstanceChannel Open()
        {
            Opened++;
            var channel = new FakeChannel(_signal, isFirst: !_created);
            _created = true;
            Channels.Add(channel);
            return channel;
        }
    }

    private sealed class FakeChannel(AutoResetEvent signal, bool isFirst) : IInstanceChannel
    {
        public bool IsFirst { get; } = isFirst;

        public int Signals { get; private set; }

        public bool Disposed { get; private set; }

        public void SignalFirst()
        {
            Signals++;
            signal.Set();
        }

        public bool Wait(CancellationToken cancellationToken) => WaitHandle.WaitAny([signal, cancellationToken.WaitHandle]) == 0;

        public void Dispose() => Disposed = true;
    }

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public void TheFirstStart_RunsTheApp()
    {
        var system = new FakeSystem();

        using var first = SingleInstance.Enter([], system.Open);

        Assert.True(first.IsPrimary);
        Assert.Equal(1, system.Opened);
    }

    [Fact]
    public void ASecondStart_WakesTheFirst_AndIsToldToExit()
    {
        var system = new FakeSystem();
        using var first = SingleInstance.Enter([], system.Open);
        using var woken = new ManualResetEventSlim();
        first.Requested += woken.Set;

        using var second = SingleInstance.Enter([], system.Open);

        Assert.False(second.IsPrimary);
        Assert.Equal(1, system.Channels[1].Signals);
        Assert.True(system.Channels[1].Disposed);
        Assert.True(woken.Wait(Patience, TestContext.Current.CancellationToken), "The first instance did not get the request.");
    }

    [Fact]
    public void EachLaterStart_RaisesOneRequest()
    {
        var system = new FakeSystem();
        using var first = SingleInstance.Enter([], system.Open);
        using var enough = new CountdownEvent(3);
        var threads = new List<bool>();
        first.Requested += () =>
        {
            lock (threads)
            {
                threads.Add(Thread.CurrentThread.IsThreadPoolThread);
            }

            enough.Signal();
        };

        for (var i = 0; i < 3; i++)
        {
            using var later = SingleInstance.Enter([], system.Open);
            Assert.False(later.IsPrimary);

            // The auto-reset event holds one signal at a time: wait for this one to be taken before the next.
            SpinWait.SpinUntil(() => enough.CurrentCount == 3 - (i + 1), Patience);
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
        using var running = SingleInstance.Enter([], system.Open);
        var woken = 0;
        running.Requested += () => Interlocked.Increment(ref woken);

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
        using var second = new ManualResetEventSlim();
        var calls = 0;
        first.Requested += () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("boom");
            }

            second.Set();
        };

        using (SingleInstance.Enter([], system.Open))
        {
        }

        SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, Patience);
        using (SingleInstance.Enter([], system.Open))
        {
        }

        Assert.True(second.Wait(Patience, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheNamesMatchTheInstaller()
    {
        Assert.Equal("MurmurSingleInstance", SingleInstance.MutexName);
        Assert.Equal("MurmurShowApp", SingleInstance.EventName);
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
