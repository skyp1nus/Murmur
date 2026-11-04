using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Calls;
using Murmur.Core.Recording;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

/// <summary>The call autopilot of #44 with a fake detector: what a detected call does, the toast's buttons, the switch and "Try it".</summary>
public sealed class CallDetectionTests
{
    private static readonly CallInfo Teams = new("Microsoft Teams", "ms-teams", 100);
    private static readonly CallInfo Zoom = new("Zoom", "Zoom", 200);

    internal sealed class FakeCallDetector : ICallDetector
    {
        public bool IsRunning { get; private set; }

        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public event Action<CallInfo>? CallStarted;

        public event Action<CallInfo>? CallEnded;

        public void Start()
        {
            IsRunning = true;
            Starts++;
        }

        public void Stop()
        {
            IsRunning = false;
            Stops++;
        }

        public void RaiseStarted(CallInfo call) => CallStarted?.Invoke(call);

        public void RaiseEnded(CallInfo call) => CallEnded?.Invoke(call);

        public void Dispose() => Stop();
    }

    internal sealed class FakeToastHost : ICallToastHost
    {
        public CallToastViewModel? Shown { get; private set; }

        public List<CallToastViewModel> History { get; } = [];

        public void Show(CallToastViewModel toast)
        {
            Shown = toast;
            History.Add(toast);
        }

        public void Close() => Shown = null;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(AppSettings? settings = null, bool canRecord = true)
        {
            Store = new FakeSettingsStore(settings);
            CanRecord = canRecord;
            Coordinator = new CallCoordinator(
                Detector,
                Store,
                Recorder,
                Toasts,
                new CallActions(
                    StartRecording: async () =>
                    {
                        StartRequests++;
                        await Recorder.StartAsync(new RecordingOptions(), TestContext.Current.CancellationToken);
                    },
                    StopRecording: async () => await Recorder.StopAsync(TestContext.Current.CancellationToken),
                    OpenApp: () => Opens++,
                    CanRecord: () => CanRecord),
                action => action());
        }

        public FakeCallDetector Detector { get; } = new();

        public FakeToastHost Toasts { get; } = new();

        public FakeRecorder Recorder { get; } = new();

        public FakeSettingsStore Store { get; }

        public CallCoordinator Coordinator { get; }

        public bool CanRecord { get; set; }

        public int StartRequests { get; private set; }

        public int Opens { get; private set; }

        public void Dispose() => Coordinator.Dispose();
    }

    // ---- Detection on/off --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithDetectCallsOn_TheDetectorRuns_AndTurningItOff_StopsIt()
    {
        using var rig = new Rig();
        Assert.False(rig.Detector.IsRunning);

        rig.Coordinator.Start();
        Assert.True(rig.Detector.IsRunning);

        await rig.Store.SaveAsync(rig.Store.Current with { DetectCalls = false }, TestContext.Current.CancellationToken);
        Assert.False(rig.Detector.IsRunning);

        await rig.Store.SaveAsync(rig.Store.Current with { DetectCalls = true }, TestContext.Current.CancellationToken);
        Assert.True(rig.Detector.IsRunning);
        Assert.Equal(2, rig.Detector.Starts);
    }

    [Fact]
    public void WithDetectCallsOff_NothingIsWatched_AndALateCallShowsNothing()
    {
        using var rig = new Rig(new AppSettings { DetectCalls = false });
        rig.Coordinator.Start();

        rig.Detector.RaiseStarted(Teams);

        Assert.False(rig.Detector.IsRunning);
        Assert.Equal(0, rig.Detector.Starts);
        Assert.Null(rig.Toasts.Shown);
    }

    [Fact]
    public async Task TurningDetectCallsOff_ClosesTheOffer()
    {
        using var rig = new Rig();
        rig.Coordinator.Start();
        rig.Detector.RaiseStarted(Teams);
        Assert.NotNull(rig.Toasts.Shown);

        await rig.Store.SaveAsync(rig.Store.Current with { DetectCalls = false }, TestContext.Current.CancellationToken);

        Assert.Null(rig.Toasts.Shown);
    }

    [Fact]
    public void Dispose_StopsTheDetector_AndClosesTheToast()
    {
        var rig = new Rig();
        rig.Coordinator.Start();
        rig.Detector.RaiseStarted(Teams);

        rig.Dispose();
        rig.Detector.RaiseStarted(Zoom);

        Assert.False(rig.Detector.IsRunning);
        Assert.Null(rig.Toasts.Shown);
        Assert.Equal(0, rig.Store.SubscriberCount);
    }

    // ---- A call starts -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ADetectedCall_ShowsTheOffer()
    {
        using var rig = new Rig();
        rig.Coordinator.Start();

        rig.Detector.RaiseStarted(Teams);

        var toast = Assert.IsType<CallToastViewModel>(rig.Toasts.Shown);
        Assert.Equal(CallToastKind.Offer, toast.Kind);
        Assert.Equal(Teams, toast.Call);
        Assert.True(toast.ShowAlwaysRecord);
        Assert.Equal(0, rig.StartRequests);
    }

    [Fact]
    public async Task NoToast_WhileMurmurRecords()
    {
        using var rig = new Rig();
        rig.Coordinator.Start();
        await rig.Recorder.StartAsync(new RecordingOptions(), TestContext.Current.CancellationToken);

        rig.Detector.RaiseStarted(Teams);

        Assert.Null(rig.Toasts.Shown);
        Assert.Empty(rig.Toasts.History);
    }

    [Fact]
    public void NoToast_WhenTheAppCannotRecordNow()
    {
        using var rig = new Rig(canRecord: false);
        rig.Coordinator.Start();

        rig.Detector.RaiseStarted(Teams);

        Assert.Null(rig.Toasts.Shown);
    }

    [Fact]
    public async Task ARecordingStartedAnotherWay_ClosesTheOffer()
    {
        using var rig = new Rig();
        rig.Coordinator.Start();
        rig.Detector.RaiseStarted(Teams);

        await rig.Recorder.StartAsync(new RecordingOptions(), TestContext.Current.CancellationToken);

        Assert.Null(rig.Toasts.Shown);
    }

    [Fact]
    public void TheEndOfTheCall_ClosesItsOffer_ButNotAnotherOne()
    {
        using var rig = new Rig();
        rig.Coordinator.Start();
        rig.Detector.RaiseStarted(Teams);

        rig.Detector.RaiseEnded(Zoom);
        Assert.NotNull(rig.Toasts.Shown);

        rig.Detector.RaiseEnded(Teams);
        Assert.Null(rig.Toasts.Shown);
    }

    // ---- Allowlist ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAllowlistedApp_StartsRecording_AndShowsTheAutoRecordToast()
    {
        using var rig = new Rig(new AppSettings { CallAllowlist = ["MS-Teams.exe"] });
        rig.Coordinator.Start();

        rig.Detector.RaiseStarted(Teams);
        await rig.Coordinator.LastAction;

        Assert.Equal(1, rig.StartRequests);
        Assert.Equal(RecorderState.Recording, rig.Recorder.State);
        var toast = Assert.IsType<CallToastViewModel>(rig.Toasts.Shown);
        Assert.Equal(CallToastKind.AutoRecording, toast.Kind);
        Assert.False(toast.ShowAlwaysRecord);
    }

    [Fact]
    public async Task AnAllowlistedApp_WhoseRecordingDoesNotStart_ShowsNoAutoRecordToast()
    {
        using var rig = new Rig(new AppSettings { CallAllowlist = ["ms-teams"] });
        rig.Recorder.StartFailure = new InvalidOperationException("no device");
        rig.Coordinator.Start();

        rig.Detector.RaiseStarted(Teams);
        await rig.Coordinator.LastAction;

        Assert.Null(rig.Toasts.Shown);
    }

    [Fact]
    public async Task OpenAndStop_OnTheAutoRecordToast()
    {
        using var rig = new Rig(new AppSettings { CallAllowlist = ["ms-teams"] });
        rig.Coordinator.Start();
        rig.Detector.RaiseStarted(Teams);
        await rig.Coordinator.LastAction;

        rig.Toasts.Shown!.OpenCommand.Execute(null);
        Assert.Equal(1, rig.Opens);
        Assert.Null(rig.Toasts.Shown);
        Assert.Equal(RecorderState.Recording, rig.Recorder.State);

        rig.Detector.RaiseEnded(Teams);
        rig.Detector.RaiseStarted(Teams); // while recording: nothing
        Assert.Null(rig.Toasts.Shown);

        await rig.Recorder.StopAsync(TestContext.Current.CancellationToken);
        rig.Detector.RaiseStarted(Teams);
        await rig.Coordinator.LastAction;
        rig.Toasts.Shown!.StopCommand.Execute(null);
        await rig.Coordinator.LastAction;
        Assert.Equal(RecorderState.Idle, rig.Recorder.State);
        Assert.Equal(2, rig.Recorder.Stops);
        Assert.Null(rig.Toasts.Shown);
    }

    [Fact]
    public async Task TheAutoRecordToast_ClosesWhenTheRecordingStops_AndOutlivesTheCall()
    {
        using var rig = new Rig(new AppSettings { CallAllowlist = ["ms-teams"] });
        rig.Coordinator.Start();
        rig.Detector.RaiseStarted(Teams);
        await rig.Coordinator.LastAction;

        rig.Detector.RaiseEnded(Teams);
        Assert.NotNull(rig.Toasts.Shown);

        await rig.Recorder.StopAsync(TestContext.Current.CancellationToken);
        Assert.Null(rig.Toasts.Shown);
    }

    // ---- The offer's buttons -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Record_StartsARecording_WithoutTouchingTheAllowlist()
    {
        using var rig = new Rig();
        rig.Coordinator.Start();
        rig.Detector.RaiseStarted(Teams);

        rig.Toasts.Shown!.RecordCommand.Execute(null);
        await rig.Coordinator.LastAction;

        Assert.Null(rig.Toasts.Shown);
        Assert.Equal(1, rig.StartRequests);
        Assert.Equal(RecorderState.Recording, rig.Recorder.State);
        Assert.Empty(rig.Store.Current.CallAllowlist);
    }

    [Fact]
    public async Task RecordWithAlwaysRecord_AddsTheProcessName_AndTheNextCallStartsByItself()
    {
        using var rig = new Rig(new AppSettings { CallAllowlist = ["slack"] });
        rig.Coordinator.Start();
        rig.Detector.RaiseStarted(Teams);

        var toast = rig.Toasts.Shown!;
        toast.AlwaysRecord = true;
        toast.RecordCommand.Execute(null);
        await rig.Coordinator.LastAction;
        await SettingsWriter.For(rig.Store).LastSave;

        Assert.Equal(["slack", "ms-teams"], rig.Store.Current.CallAllowlist);

        await rig.Recorder.StopAsync(TestContext.Current.CancellationToken);
        rig.Detector.RaiseEnded(Teams);
        rig.Detector.RaiseStarted(Teams);
        await rig.Coordinator.LastAction;
        Assert.Equal(CallToastKind.AutoRecording, rig.Toasts.Shown!.Kind);
        Assert.Equal(2, rig.StartRequests);
    }

    [Fact]
    public void NotNow_DismissesTheOffer_ForThisCallSession()
    {
        using var rig = new Rig();
        rig.Coordinator.Start();
        rig.Detector.RaiseStarted(Teams);

        rig.Toasts.Shown!.NotNowCommand.Execute(null);

        Assert.Null(rig.Toasts.Shown);
        Assert.Equal(0, rig.StartRequests);
        Assert.Single(rig.Toasts.History);
        Assert.Empty(rig.Store.Current.CallAllowlist);
    }

    [Fact]
    public void NotNowWithAlwaysRecordChecked_DoesNotAllowlist()
    {
        using var rig = new Rig();
        rig.Coordinator.Start();
        rig.Detector.RaiseStarted(Teams);

        rig.Toasts.Shown!.AlwaysRecord = true;
        rig.Toasts.Shown!.DismissCommand.Execute(null);

        Assert.Null(rig.Toasts.Shown);
        Assert.Equal(0, rig.Store.SaveCount);
    }

    [Fact]
    public void AToastAnswersOnce_AndAReplacedToast_IsIgnored()
    {
        using var rig = new Rig();
        rig.Coordinator.Start();
        rig.Detector.RaiseStarted(Teams);
        var first = rig.Toasts.Shown!;
        rig.Detector.RaiseStarted(Zoom);

        first.RecordCommand.Execute(null);

        Assert.Equal(0, rig.StartRequests);
        Assert.Equal(Zoom, rig.Toasts.Shown!.Call);
    }

    // ---- Try it ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TryIt_ShowsTheOffer_ForASampleApp_WithoutTheAllowlistCheckbox()
    {
        using var rig = new Rig();
        rig.Coordinator.Start();

        rig.Coordinator.ShowSample();

        var toast = rig.Toasts.Shown!;
        Assert.True(toast.IsSample);
        Assert.Equal(CallToastKind.Offer, toast.Kind);
        Assert.Equal(Strings.Calls_SampleApp, toast.Call.AppName);
        Assert.False(toast.ShowAlwaysRecord);
    }

    [Fact]
    public async Task TheSamplesRecord_StartsARecording_ButNeverAllowlists()
    {
        using var rig = new Rig();
        rig.Coordinator.Start();
        rig.Coordinator.ShowSample();

        rig.Toasts.Shown!.AlwaysRecord = true;
        rig.Toasts.Shown!.RecordCommand.Execute(null);
        await rig.Coordinator.LastAction;

        Assert.Equal(1, rig.StartRequests);
        Assert.Equal(0, rig.Store.SaveCount);
    }

    [Fact]
    public void TheSettingsTryIt_ReachesTheCoordinatorThroughTheSettingsPage()
    {
        var store = new FakeSettingsStore();
        using var settings = new SettingsViewModel(store, new FakeCapture(), post: a => a(), audioSessions: new SupportedMonitor());
        var asked = 0;
        settings.SampleCallToastRequested += () => asked++;

        settings.Calls.TryItCommand.Execute(null);

        Assert.Equal(1, asked);
    }

    // ---- Settings → Detect calls -------------------------------------------------------------------------------------------

    private sealed class SupportedMonitor : IAudioSessionMonitor
    {
        public bool IsSupported => true;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public AudioActivitySnapshot Snapshot() => new(false, []);

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

    [Fact]
    public void TheDetectCallsRow_ExistsOnlyWithASupportedMonitor()
    {
        var store = new FakeSettingsStore();
        using var none = new SettingsViewModel(store, new FakeCapture(), post: a => a());
        using var unsupported = new SettingsViewModel(store, new FakeCapture(), post: a => a(), audioSessions: new NullAudioSessionMonitor());
        using var supported = new SettingsViewModel(store, new FakeCapture(), post: a => a(), audioSessions: new SupportedMonitor());

        Assert.False(none.CallDetectionSupported);
        Assert.False(unsupported.CallDetectionSupported);
        Assert.True(supported.CallDetectionSupported);
    }

    [Fact]
    public async Task TheSwitch_SavesDetectCalls_AndFollowsTheStore()
    {
        var store = new FakeSettingsStore();
        using var settings = new SettingsViewModel(store, new FakeCapture(), post: a => a(), audioSessions: new SupportedMonitor());
        Assert.True(settings.Calls.DetectCalls);

        settings.Calls.DetectCalls = false;
        await settings.Calls.LastSave;
        Assert.False(store.Current.DetectCalls);
        Assert.Null(settings.Calls.Error);

        await store.SaveAsync(store.Current with { DetectCalls = true }, TestContext.Current.CancellationToken);
        Assert.True(settings.Calls.DetectCalls);
    }

    [Fact]
    public async Task AFailedSave_ShowsTheErrorInline()
    {
        var store = new FakeSettingsStore { SaveFailure = new IOException("disk full") };
        using var settings = new SettingsViewModel(store, new FakeCapture(), post: a => a(), audioSessions: new SupportedMonitor());

        settings.Calls.DetectCalls = false;
        await settings.Calls.LastSave;

        Assert.True(settings.Calls.HasError);
        Assert.Contains("disk full", settings.Calls.Error, StringComparison.Ordinal);
    }

    // ---- The toast's texts -------------------------------------------------------------------------------------------------

    [Fact]
    public void TheToastTexts_NameTheApp_InEnglishAndUkrainian()
    {
        var offer = new CallToastViewModel(Teams, CallToastKind.Offer);
        var auto = new CallToastViewModel(Zoom, CallToastKind.AutoRecording);

        Assert.Equal("Call detected", offer.Title);
        Assert.Equal("Microsoft Teams is playing call audio.", offer.Body);
        Assert.Equal("Recording Zoom call", auto.Title);
        Assert.True(auto.IsAutoRecording);
        Assert.False(auto.IsOffer);

        using (UiCulture.Override("uk"))
        {
            Assert.Equal("Виявлено дзвінок", offer.Title);
            Assert.Equal("Microsoft Teams відтворює звук дзвінка.", offer.Body);
            Assert.Equal("Запис дзвінка: Zoom", auto.Title);
        }
    }

    // ---- Through the real Recording flow -----------------------------------------------------------------------------------

    [Fact]
    public async Task Record_GoesThroughTheRecordingFlow_WithTheSavedDefaultSources()
    {
        using var rig = new OnboardingRig(new AppSettings { OnboardingCompleted = true, RecordSystemAudioByDefault = false });
        var shell = rig.Shell;
        var detector = new FakeCallDetector();
        var toasts = new FakeToastHost();
        using var coordinator = new CallCoordinator(
            detector,
            rig.Store,
            rig.Recorder,
            toasts,
            new CallActions(
                () => shell.Recording.RecorderIsIdle ? shell.ToggleRecordingFromHotkeyAsync() : Task.CompletedTask,
                () => shell.Recording.StopAsync(),
                shell.ShowRecording,
                () => !shell.IsOnboarding),
            a => a());
        coordinator.Start();

        detector.RaiseStarted(Teams);
        toasts.Shown!.RecordCommand.Execute(null);
        await coordinator.LastAction;

        var options = Assert.Single(rig.Recorder.Starts);
        Assert.True(options.Microphone);
        Assert.False(options.SystemAudio);
        Assert.Same(shell.Recording, shell.CurrentPage);
    }
}
