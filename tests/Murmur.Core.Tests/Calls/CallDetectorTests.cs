using Murmur.Core.Calls;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Calls;

public sealed class CallDetectorTests
{
    private const int OwnPid = 4242;

    private sealed class FakeMonitor : IAudioSessionMonitor
    {
        private readonly Lock _gate = new();
        private AudioActivitySnapshot _snapshot = new(false, []);

        public bool IsSupported => true;

        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public int Snapshots { get; private set; }

        public bool HasSubscribers => Changed is not null;

        public event Action? Changed;

        /// <summary>How many of the next <see cref="Snapshot"/> calls throw (the audio service restarting).</summary>
        public int FailNext { get; set; }

        /// <summary>The threads <see cref="Snapshot"/> was called on.</summary>
        public List<int> SnapshotThreads { get; } = [];

        public AudioActivitySnapshot Snapshot()
        {
            lock (_gate)
            {
                Snapshots++;
                SnapshotThreads.Add(Environment.CurrentManagedThreadId);
                if (FailNext > 0)
                {
                    FailNext--;
                    throw new InvalidOperationException("audio service restarting");
                }

                return _snapshot;
            }
        }

        /// <summary>Sets what the audio stack reports and raises <see cref="Changed"/>, like a session event would.</summary>
        public void Set(bool mic, params RenderingProcess[] rendering)
        {
            lock (_gate)
            {
                _snapshot = new AudioActivitySnapshot(mic, rendering);
            }

            Changed?.Invoke();
        }

        /// <summary>Sets the state without an event (a change the monitor missed).</summary>
        public void SetSilently(bool mic, params RenderingProcess[] rendering)
        {
            lock (_gate)
            {
                _snapshot = new AudioActivitySnapshot(mic, rendering);
            }
        }

        public void Start() => Starts++;

        public void Stop() => Stops++;

        public void Dispose()
        {
        }
    }

    private static readonly RenderingProcess Teams = new(100, "ms-teams");
    private static readonly RenderingProcess Zoom = new(200, "Zoom.exe");
    private static readonly RenderingProcess Murmur = new(OwnPid, "Murmur");

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Detector = new CallDetector(Monitor, Time, OwnPid);
            Detector.CallStarted += Started.Add;
            Detector.CallEnded += Ended.Add;
        }

        public FakeMonitor Monitor { get; } = new();

        public ManualTimeProvider Time { get; } = new();

        public CallDetector Detector { get; }

        public List<CallInfo> Started { get; } = [];

        public List<CallInfo> Ended { get; } = [];

        public void Advance(double seconds) => Time.Advance(TimeSpan.FromSeconds(seconds));

        public void Dispose() => Detector.Dispose();
    }

    [Fact]
    public void MicAndAnotherAppsPlayback_ForThreeSeconds_IsACall()
    {
        using var rig = new Rig();
        rig.Detector.Start();

        rig.Monitor.Set(true, Teams);
        rig.Advance(2.9);
        Assert.Empty(rig.Started);

        rig.Advance(0.1);
        var call = Assert.Single(rig.Started);
        Assert.Equal(new CallInfo("Microsoft Teams", "ms-teams", 100), call);
        Assert.Equal(call, rig.Detector.CurrentCall);
    }

    [Fact]
    public void PlaybackWithoutTheMic_OrTheMicWithoutPlayback_IsNoCall()
    {
        using var rig = new Rig();
        rig.Detector.Start();

        rig.Monitor.Set(false, Teams);
        rig.Advance(10);
        rig.Monitor.Set(true);
        rig.Advance(10);

        Assert.Empty(rig.Started);
    }

    [Fact]
    public void TheThreeSeconds_MustBeContinuous()
    {
        using var rig = new Rig();
        rig.Detector.Start();

        rig.Monitor.Set(true, Teams);
        rig.Advance(2);
        rig.Monitor.Set(false, Teams); // the mic pauses
        rig.Monitor.Set(true, Teams);
        rig.Advance(2);
        Assert.Empty(rig.Started);

        rig.Monitor.Set(true); // the playback pauses
        rig.Monitor.Set(true, Teams);
        rig.Advance(2.5);
        Assert.Empty(rig.Started);

        rig.Advance(0.5);
        Assert.Single(rig.Started);
    }

    [Fact]
    public void MurmursOwnPlayback_NeverCounts_NorDoesTheSystemSoundsSession()
    {
        using var rig = new Rig();
        rig.Detector.Start();

        rig.Monitor.Set(true, Murmur, new RenderingProcess(0, "System Sounds"));
        rig.Advance(60);

        Assert.Empty(rig.Started);
        Assert.False(rig.Detector.IsTimerScheduled);
    }

    [Fact]
    public void ByDefault_TheOwnProcessIsThisProcess()
    {
        var monitor = new FakeMonitor();
        var time = new ManualTimeProvider();
        using var detector = new CallDetector(monitor, time);
        var started = new List<CallInfo>();
        detector.CallStarted += started.Add;
        detector.Start();

        monitor.Set(true, new RenderingProcess(Environment.ProcessId, "testhost"));
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Empty(started);
    }

    [Fact]
    public void OneCallStarted_PerCallSession()
    {
        using var rig = new Rig();
        rig.Detector.Start();

        rig.Monitor.Set(true, Teams);
        rig.Advance(3);
        rig.Monitor.Set(true, Teams, Zoom); // another app joins in
        rig.Advance(20);
        rig.Monitor.Set(false, Teams); // the user mutes
        rig.Advance(20);
        rig.Monitor.Set(true, Teams);
        rig.Advance(20);

        Assert.Single(rig.Started);
        Assert.Empty(rig.Ended);
    }

    [Fact]
    public void TheSessionEnds_AfterMicAndTheAppsPlayback_AreInactiveFor30Seconds()
    {
        using var rig = new Rig();
        rig.Detector.Start();
        rig.Monitor.Set(true, Teams);
        rig.Advance(3);

        rig.Monitor.Set(false);
        rig.Advance(29.9);
        Assert.Empty(rig.Ended);

        rig.Advance(0.1);
        var ended = Assert.Single(rig.Ended);
        Assert.Equal(rig.Started[0], ended);
        Assert.Null(rig.Detector.CurrentCall);
        Assert.False(rig.Detector.IsTimerScheduled);
    }

    [Fact]
    public void EitherTheMicOrTheAppsPlayback_KeepsTheSessionAlive()
    {
        using var rig = new Rig();
        rig.Detector.Start();
        rig.Monitor.Set(true, Teams);
        rig.Advance(3);

        rig.Monitor.Set(true); // only the mic
        rig.Advance(40);
        rig.Monitor.Set(false, Teams); // only the playback
        rig.Advance(40);
        rig.Monitor.Set(false, Zoom); // only another app's playback: that does not keep Teams' call
        rig.Advance(25);
        rig.Monitor.Set(false); // Zoom stops; the 30 s ran from when Teams stopped
        rig.Advance(5);

        Assert.Single(rig.Ended);
    }

    [Fact]
    public void TheAppIsFollowedByName_WhenItsAudioMovesToAnotherProcess()
    {
        using var rig = new Rig();
        rig.Detector.Start();
        rig.Monitor.Set(true, Teams);
        rig.Advance(3);

        rig.Monitor.Set(false, new RenderingProcess(101, "MS-Teams.exe"));
        rig.Advance(60);

        Assert.Empty(rig.Ended);
    }

    [Fact]
    public void AfterTheSessionEnds_TheNextCallIsDetectedAgain()
    {
        using var rig = new Rig();
        rig.Detector.Start();
        rig.Monitor.Set(true, Teams);
        rig.Advance(3);
        rig.Monitor.Set(false);
        rig.Advance(30);

        rig.Monitor.Set(true, Zoom);
        rig.Advance(3);

        Assert.Equal(2, rig.Started.Count);
        Assert.Equal(new CallInfo("Zoom", "Zoom", 200), rig.Started[1]);
    }

    [Fact]
    public void NoTimer_WhileNothingCountsDown()
    {
        using var rig = new Rig();
        rig.Detector.Start();
        rig.Advance(0); // the first evaluation, queued on the timer
        Assert.False(rig.Detector.IsTimerScheduled);
        Assert.Equal(0, rig.Time.ActiveTimers);

        rig.Monitor.Set(false, Teams); // playback alone: nothing to wait for
        Assert.Equal(0, rig.Time.ActiveTimers);

        rig.Monitor.Set(true, Teams);
        Assert.Equal(1, rig.Time.ActiveTimers);
        rig.Advance(3);

        // In the call with everything active, the detector waits for the monitor's events.
        Assert.False(rig.Detector.IsTimerScheduled);
        Assert.Equal(0, rig.Time.ActiveTimers);
    }

    [Fact]
    public void WhileCountingDown_ItReevaluatesAtMostOnceASecond()
    {
        using var rig = new Rig();
        rig.Detector.Start();
        rig.Monitor.Set(true, Teams);
        var before = rig.Monitor.Snapshots;

        rig.Advance(2.5);

        Assert.InRange(rig.Monitor.Snapshots - before, 1, 3);
    }

    [Fact]
    public void AChangeTheMonitorMissed_IsCaughtByTheCountdownTimer()
    {
        using var rig = new Rig();
        rig.Detector.Start();
        rig.Monitor.Set(true, Teams);
        rig.Advance(3);

        rig.Monitor.Set(false);
        rig.Advance(10);
        rig.Monitor.SetSilently(true, Teams); // no event: the timer still running for the 30 s sees it
        rig.Advance(1);
        rig.Monitor.SetSilently(false);
        rig.Advance(25);

        Assert.Empty(rig.Ended);
    }

    [Fact]
    public void WhenStopped_NothingIsWatched_AndNoTimerRuns()
    {
        using var rig = new Rig();
        rig.Detector.Start();
        Assert.True(rig.Detector.IsRunning);
        Assert.True(rig.Monitor.HasSubscribers);
        Assert.Equal(1, rig.Monitor.Starts);
        rig.Monitor.Set(true, Teams);

        rig.Detector.Stop();
        var snapshots = rig.Monitor.Snapshots;
        rig.Monitor.Set(true, Teams, Zoom);
        rig.Advance(60);

        Assert.False(rig.Detector.IsRunning);
        Assert.False(rig.Monitor.HasSubscribers);
        Assert.Equal(1, rig.Monitor.Stops);
        Assert.Equal(snapshots, rig.Monitor.Snapshots);
        Assert.Equal(0, rig.Time.ActiveTimers);
        Assert.Empty(rig.Started);
    }

    [Fact]
    public void NeverStarted_ReadsNothing()
    {
        using var rig = new Rig();

        rig.Monitor.Set(true, Teams);
        rig.Advance(60);

        Assert.Equal(0, rig.Monitor.Starts);
        Assert.Equal(0, rig.Monitor.Snapshots);
        Assert.Empty(rig.Started);
    }

    [Fact]
    public void ACallInProgressWhenStarted_IsDetectedFromTheStart()
    {
        using var rig = new Rig();
        rig.Monitor.SetSilently(true, Teams);

        rig.Detector.Start();
        rig.Advance(3);

        Assert.Single(rig.Started);
    }

    [Fact]
    public void StartAndStop_AreIdempotent_AndAStoppedDetectorCanStartAgain()
    {
        using var rig = new Rig();
        rig.Detector.Start();
        rig.Detector.Start();
        Assert.Equal(1, rig.Monitor.Starts);

        rig.Detector.Stop();
        rig.Detector.Stop();
        Assert.Equal(1, rig.Monitor.Stops);

        rig.Detector.Start();
        rig.Monitor.Set(true, Teams);
        rig.Advance(3);
        Assert.Single(rig.Started);
    }

    [Fact]
    public void ATickThatCannotReadTheSessions_DoesNotStallTheEndOfTheCall()
    {
        using var rig = new Rig();
        rig.Detector.Start();
        rig.Monitor.Set(true, Teams);
        rig.Advance(3);
        rig.Monitor.Set(false);

        rig.Monitor.FailNext = 1; // the next tick throws; no Changed follows
        rig.Advance(30);

        Assert.Single(rig.Ended);
        Assert.Null(rig.Detector.CurrentCall);
    }

    [Fact]
    public void ATickThatCannotReadTheSessions_DoesNotStallTheStartOfACall()
    {
        using var rig = new Rig();
        rig.Detector.Start();
        rig.Monitor.Set(true, Teams);

        rig.Monitor.FailNext = 3; // the ticks at 1, 2 and 3 s throw; no Changed follows
        rig.Advance(3.5);
        Assert.Empty(rig.Started);

        rig.Advance(1);
        Assert.Single(rig.Started);
    }

    [Fact]
    public void AFailedFirstReading_IsRetried_SoACallInProgressIsStillDetected()
    {
        using var rig = new Rig();
        rig.Monitor.SetSilently(true, Teams); // the call runs already; no Changed will come
        rig.Monitor.FailNext = 2; // the first reading and its first retry fail

        rig.Detector.Start();
        rig.Advance(0);
        Assert.True(rig.Detector.IsTimerScheduled);

        rig.Advance(1);
        Assert.True(rig.Detector.IsTimerScheduled);

        rig.Advance(1 + 3); // the reading at 2 s succeeds, the 3 s run from there
        Assert.Single(rig.Started);
    }

    [Fact]
    public void AFailedReading_AfterOneSucceeded_WithNothingCountingDown_WaitsForTheNextChange()
    {
        using var rig = new Rig();
        rig.Detector.Start();
        rig.Advance(0); // a good first reading: idle

        rig.Monitor.FailNext = 1;
        rig.Monitor.Set(false, Teams); // playback alone; this reading fails

        Assert.False(rig.Detector.IsTimerScheduled);
        Assert.Equal(0, rig.Time.ActiveTimers);
    }

    [Fact]
    public void Start_QueuesTheFirstEvaluation_OffTheCallersThread()
    {
        using var rig = new Rig();

        rig.Detector.Start();
        Assert.Equal(0, rig.Monitor.Snapshots);
        Assert.True(rig.Detector.IsTimerScheduled);

        rig.Advance(0);
        Assert.Equal(1, rig.Monitor.Snapshots);
    }

    [Fact]
    public void Start_UsesThePool_WithTheSystemClock()
    {
        var monitor = new FakeMonitor();
        using var detector = new CallDetector(monitor, TimeProvider.System, OwnPid);

        detector.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (monitor.Snapshots == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }

        Assert.NotEmpty(monitor.SnapshotThreads);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, monitor.SnapshotThreads);
    }

    [Fact]
    public void AThrowingSubscriber_IsContained_AndTheOthersStillHearTheCall()
    {
        using var rig = new Rig();
        rig.Detector.CallStarted += _ => throw new InvalidOperationException("bad handler");
        var late = new List<CallInfo>();
        rig.Detector.CallStarted += late.Add;
        rig.Detector.Start();

        rig.Monitor.Set(true, Teams);
        rig.Advance(3);

        Assert.Single(rig.Started);
        Assert.Single(late);
    }

    [Fact]
    public async Task StartAndStop_FromManyThreads_LeaveTheMonitorBalanced()
    {
        var monitor = new FakeMonitor();
        using var detector = new CallDetector(monitor, new ManualTimeProvider(), OwnPid);

        var workers = Enumerable.Range(0, 8).Select(i => Task.Run(
            () =>
            {
                for (var n = 0; n < 200; n++)
                {
                    if ((n + i) % 2 == 0)
                    {
                        detector.Start();
                    }
                    else
                    {
                        detector.Stop();
                    }
                }
            },
            TestContext.Current.CancellationToken)).ToArray();
        await Task.WhenAll(workers);
        detector.Stop();

        Assert.False(detector.IsRunning);
        Assert.Equal(monitor.Starts, monitor.Stops);
        Assert.False(monitor.HasSubscribers);
    }

    [Fact]
    public void AfterDispose_StartThrows()
    {
        var rig = new Rig();
        rig.Dispose();

        Assert.Throws<ObjectDisposedException>(rig.Detector.Start);
    }

    [Fact]
    public void AMonitorThatFailsToReport_IsAskedAgainLater()
    {
        var monitor = new ThrowingMonitor();
        var time = new ManualTimeProvider();
        using var detector = new CallDetector(monitor, time, OwnPid);
        var started = new List<CallInfo>();
        detector.CallStarted += started.Add;
        detector.Start();

        monitor.Fail = false;
        monitor.Raise();
        time.Advance(TimeSpan.FromSeconds(3));

        Assert.Single(started);
    }

    private sealed class ThrowingMonitor : IAudioSessionMonitor
    {
        public bool Fail { get; set; } = true;

        public bool IsSupported => true;

        public event Action? Changed;

        public void Raise() => Changed?.Invoke();

        public AudioActivitySnapshot Snapshot() =>
            Fail ? throw new InvalidOperationException("audio service restarting") : new AudioActivitySnapshot(true, [Teams]);

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}
