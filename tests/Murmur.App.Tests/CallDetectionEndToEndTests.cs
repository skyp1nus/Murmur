using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Calls;
using Murmur.Core.Recording;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

/// <summary>The real <see cref="CallDetector"/> and <see cref="CallCoordinator"/> wired to a fake monitor, a fake clock, a fake recorder and a fake toast host.</summary>
public sealed class CallDetectionEndToEndTests
{
    private const int OwnPid = 4242;
    private static readonly RenderingProcess Teams = new(100, "ms-teams");

    private sealed class Clock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<FakeTimer> _timers = [];
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_gate)
            {
                return _ticks;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(double seconds)
        {
            var target = GetTimestamp() + TimeSpan.FromSeconds(seconds).Ticks;
            while (true)
            {
                FakeTimer? due;
                lock (_gate)
                {
                    due = _timers.Where(t => t.Due <= target).MinBy(t => t.Due);
                    if (due is null)
                    {
                        _ticks = target;
                        return;
                    }

                    _ticks = Math.Max(_ticks, due.Due);
                    if (due.Period > 0)
                    {
                        due.Due += due.Period;
                    }
                    else
                    {
                        _timers.Remove(due);
                    }
                }

                due.Fire();
            }
        }

        private sealed class FakeTimer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            public long Due { get; set; }

            public long Period { get; set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        Due = owner._ticks + Math.Max(0, dueTime.Ticks);
                        Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                        owner._timers.Add(this);
                    }
                }

                return true;
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Monitor : IAudioSessionMonitor
    {
        private AudioActivitySnapshot _snapshot = new(false, []);

        public bool IsSupported => true;

        public int Starts { get; private set; }

        public bool HasSubscribers => Changed is not null;

        public event Action? Changed;

        public AudioActivitySnapshot Snapshot() => _snapshot;

        public void Set(bool mic, params RenderingProcess[] rendering)
        {
            _snapshot = new AudioActivitySnapshot(mic, rendering);
            Changed?.Invoke();
        }

        public void Start() => Starts++;

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(AppSettings? settings = null)
        {
            Store = new FakeSettingsStore(settings);
            Detector = new CallDetector(Monitor, Time, OwnPid);
            Coordinator = new CallCoordinator(
                Detector,
                Store,
                Recorder,
                Toasts,
                new CallActions(
                    StartRecording: async () => await Recorder.StartAsync(new RecordingOptions(), CancellationToken.None),
                    StopRecording: async () => await Recorder.StopAsync(CancellationToken.None),
                    OpenApp: () => { }),
                action => action());
            Coordinator.Start();
        }

        public Monitor Monitor { get; } = new();

        public Clock Time { get; } = new();

        public FakeRecorder Recorder { get; } = new();

        public CallDetectionTests.FakeToastHost Toasts { get; } = new();

        public FakeSettingsStore Store { get; }

        public CallDetector Detector { get; }

        public CallCoordinator Coordinator { get; }

        public void Dispose()
        {
            Coordinator.Dispose();
            Detector.Dispose();
        }
    }

    [Fact]
    public void ATeamsLikeCall_RaisesOneToast_AfterThreeSeconds_NotBefore()
    {
        using var rig = new Rig();
        rig.Time.Advance(1);
        rig.Monitor.Set(true, Teams);
        rig.Time.Advance(2.5);
        Assert.Empty(rig.Toasts.History);

        rig.Time.Advance(1);

        var toast = Assert.Single(rig.Toasts.History);
        Assert.Equal(CallToastKind.Offer, toast.Kind);
        Assert.Equal("ms-teams", toast.Call.ProcessName);

        rig.Time.Advance(120);
        Assert.Single(rig.Toasts.History);
    }

    [Fact]
    public void NotNow_SilencesTheCallSession_ButANewCallAfter30SecondsOfSilence_AsksAgain()
    {
        using var rig = new Rig();
        rig.Monitor.Set(true, Teams);
        rig.Time.Advance(3.5);
        rig.Toasts.Shown!.NotNowCommand.Execute(null);

        rig.Time.Advance(60);
        rig.Monitor.Set(false);
        rig.Time.Advance(10);
        rig.Monitor.Set(true, Teams);
        rig.Time.Advance(10);
        Assert.Single(rig.Toasts.History);

        rig.Monitor.Set(false);
        rig.Time.Advance(31);
        rig.Monitor.Set(true, Teams);
        rig.Time.Advance(3.5);

        Assert.Equal(2, rig.Toasts.History.Count);
        Assert.NotNull(rig.Toasts.Shown);
    }

    [Fact]
    public void TheEndOfTheCall_ClosesTheOffer()
    {
        using var rig = new Rig();
        rig.Monitor.Set(true, Teams);
        rig.Time.Advance(3.5);
        Assert.NotNull(rig.Toasts.Shown);

        rig.Monitor.Set(false);
        rig.Time.Advance(31);

        Assert.Null(rig.Toasts.Shown);
    }

    [Fact]
    public void AnAllowlistedApp_RecordsByItself_AndShowsTheAutoRecordToast()
    {
        using var rig = new Rig(new AppSettings { CallAllowlist = ["MS-Teams"] });
        rig.Monitor.Set(true, Teams);
        rig.Time.Advance(3.5);

        var toast = Assert.Single(rig.Toasts.History);
        Assert.Equal(CallToastKind.AutoRecording, toast.Kind);
        Assert.NotEqual(default, rig.Recorder.State);
    }

    [Fact]
    public async Task WhileMurmurRecords_ACallShowsNothing()
    {
        using var rig = new Rig();
        await rig.Recorder.StartAsync(new RecordingOptions(), TestContext.Current.CancellationToken);

        rig.Monitor.Set(true, Teams);
        rig.Time.Advance(10);

        Assert.Empty(rig.Toasts.History);
    }

    [Fact]
    public void MurmursOwnPlayback_NeverMakesACall()
    {
        using var rig = new Rig();
        rig.Monitor.Set(true, new RenderingProcess(OwnPid, "Murmur"));
        rig.Time.Advance(60);

        Assert.Empty(rig.Toasts.History);
    }

    [Fact]
    public async Task WithDetectCallsOff_NothingSubscribesToTheMonitor()
    {
        using var rig = new Rig(new AppSettings { DetectCalls = false });
        Assert.False(rig.Monitor.HasSubscribers);
        Assert.Equal(0, rig.Monitor.Starts);
        Assert.False(rig.Detector.IsRunning);

        rig.Monitor.Set(true, Teams);
        rig.Time.Advance(10);
        Assert.Empty(rig.Toasts.History);

        await rig.Store.SaveAsync(rig.Store.Current with { DetectCalls = true }, TestContext.Current.CancellationToken);
        Assert.True(rig.Detector.IsRunning);
        await rig.Store.SaveAsync(rig.Store.Current with { DetectCalls = false }, TestContext.Current.CancellationToken);
        Assert.False(rig.Detector.IsRunning);
        Assert.False(rig.Monitor.HasSubscribers);
    }

    [Fact]
    public void ATryIt_ShowsASampleToast_AndAToastClosedByTheOs_IsADismissalForTheCallSession()
    {
        using var rig = new Rig();
        rig.Coordinator.ShowSample();
        Assert.True(rig.Toasts.Shown!.IsSample);
        rig.Toasts.CloseByUser();

        rig.Monitor.Set(true, Teams);
        rig.Time.Advance(3.5);
        Assert.Equal(2, rig.Toasts.History.Count);
        rig.Toasts.CloseByUser();

        rig.Time.Advance(60);
        Assert.Equal(2, rig.Toasts.History.Count);
        Assert.Null(rig.Toasts.Shown);
    }
}
