using Murmur.App.Localization;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Platform;
using Murmur.Core.Recording;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

/// <summary>The floating recorder orb's view model (#46): when it is shown, its state, the pill, the Mark chip, the buttons and the position.</summary>
public sealed class FloatingOrbViewModelTests : IDisposable
{
    private readonly IDisposable _english = UiCulture.Override("en");
    private readonly FakeRecorder _recorder = new();
    private readonly FakeBookmarkStore _bookmarks = new();
    private readonly FakeSettingsStore _settings = new();
    private readonly TestTime _time = new(UiHarness.Now);
    private readonly FakeMotion _motion = new();
    private readonly RecordingMarks _marks;
    private readonly List<string> _calls = [];

    public FloatingOrbViewModelTests() => _marks = new RecordingMarks(_recorder, _bookmarks);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _marks.Dispose();
        _english.Dispose();
    }

    private sealed class FakeMotion : IMotionPreference
    {
        public bool ReduceMotion { get; set; }

        public event Action? Changed;

        public void Raise() => Changed?.Invoke();

        public bool HasSubscribers => Changed is not null;
    }

    private FloatingOrbViewModel Create(bool withMarks = true, bool withSettings = true) => new(
        _recorder,
        new FloatingOrbActions(
            PauseResume: () =>
            {
                _calls.Add("pause");
                return Task.CompletedTask;
            },
            Stop: () =>
            {
                _calls.Add("stop");
                return Task.CompletedTask;
            },
            Open: () => _calls.Add("open")),
        withMarks ? _marks : null,
        withSettings ? _settings : null,
        _motion,
        _time,
        action => action());

    private async Task<FloatingOrbViewModel> RecordingAwayAsync()
    {
        var orb = Create();
        await _recorder.StartAsync(new RecordingOptions(), Ct);
        orb.IsRecorderInView = false;
        return orb;
    }

    // ---- when the orb is shown ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RecorderState.Idle, false, false)]
    [InlineData(RecorderState.Idle, true, false)]
    [InlineData(RecorderState.Recording, true, false)]
    [InlineData(RecorderState.Recording, false, true)]
    [InlineData(RecorderState.Paused, true, false)]
    [InlineData(RecorderState.Paused, false, true)]
    public async Task TheOrb_IsShownOnlyWhileRunningAndTheRecordingScreenIsNotInView(RecorderState state, bool inView, bool shown)
    {
        var orb = Create();
        await _recorder.StartAsync(new RecordingOptions(), Ct);
        if (state == RecorderState.Paused)
        {
            await _recorder.PauseAsync(Ct);
        }
        else if (state == RecorderState.Idle)
        {
            await _recorder.StopAsync(Ct);
        }

        orb.IsRecorderInView = inView;

        Assert.Equal(state, orb.State);
        Assert.Equal(shown, orb.IsOrbVisible);
    }

    [Fact]
    public async Task TheOrb_IsHiddenWhileStartingAndStopping()
    {
        var orb = Create();
        var gate = new TaskCompletionSource();
        _recorder.StopGate = gate.Task;
        await _recorder.StartAsync(new RecordingOptions(), Ct);
        Assert.True(orb.IsOrbVisible);

        var stopping = _recorder.StopAsync(Ct).AsTask();
        Assert.Equal(RecorderState.Stopping, orb.State);
        Assert.False(orb.IsOrbVisible);

        gate.SetResult();
        await stopping;
        Assert.False(orb.IsOrbVisible);
    }

    [Fact]
    public async Task TheOrb_StartsFromTheStateTheRecorderIsAlreadyIn()
    {
        await _recorder.StartAsync(new RecordingOptions(), Ct);

        var orb = Create();
        orb.IsRecorderInView = false;

        Assert.True(orb.IsOrbVisible);
        Assert.True(orb.IsRecording);
    }

    // ---- states and motion ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Recording_TurnsAndPings_PausedIsStill()
    {
        var orb = await RecordingAwayAsync();
        Assert.True(orb.IsSpinning);
        Assert.False(orb.IsPaused);

        await _recorder.PauseAsync(Ct);
        Assert.False(orb.IsSpinning);
        Assert.True(orb.IsPaused);
        Assert.Equal("Resume", orb.PauseLabel);

        await _recorder.ResumeAsync(Ct);
        Assert.True(orb.IsSpinning);
        Assert.Equal("Pause", orb.PauseLabel);
    }

    [Fact]
    public async Task UnderReducedMotion_NothingMoves_TheStateIsShownByColourOnly()
    {
        _motion.ReduceMotion = true;
        var orb = await RecordingAwayAsync();

        Assert.True(orb.ReduceMotion);
        Assert.False(orb.IsSpinning);
        Assert.True(orb.IsRecording);

        await _recorder.PauseAsync(Ct);
        Assert.True(orb.IsPaused);
        Assert.False(orb.IsSpinning);
    }

    [Fact]
    public async Task TheMotionPreference_IsFollowedWhileTheOrbIsOnScreen()
    {
        var orb = await RecordingAwayAsync();
        Assert.True(orb.IsSpinning);

        _motion.ReduceMotion = true;
        _motion.Raise();
        Assert.False(orb.IsSpinning);

        _motion.ReduceMotion = false;
        _motion.Raise();
        Assert.True(orb.IsSpinning);
    }

    [Fact]
    public async Task TheOrb_DoesNotTurnWhileItIsNotOnScreen()
    {
        var orb = await RecordingAwayAsync();

        orb.IsRecorderInView = true;

        Assert.False(orb.IsSpinning);
    }

    [Fact]
    public async Task TheStateLabel_NamesTheStateForScreenReaders()
    {
        var orb = await RecordingAwayAsync();
        Assert.Equal("Recording — show controls", orb.StateLabel);

        await _recorder.PauseAsync(Ct);
        Assert.Equal("Paused — show controls", orb.StateLabel);
    }

    // ---- the pill ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AClick_OpensThePill_AndAnotherClosesIt()
    {
        var orb = await RecordingAwayAsync();
        Assert.False(orb.IsPillOpen);

        orb.PointerPressed();
        Assert.False(orb.PointerReleased());
        Assert.True(orb.IsPillOpen);

        orb.PointerPressed();
        orb.PointerReleased();
        Assert.False(orb.IsPillOpen);
    }

    [Fact]
    public async Task ADrag_MovesTheOrb_AndDoesNotToggleThePill()
    {
        var orb = await RecordingAwayAsync();

        orb.PointerPressed();
        Assert.True(orb.PointerMoved(30, -12));
        var wasDrag = orb.PointerReleased();

        Assert.True(wasDrag);
        Assert.False(orb.IsPillOpen);
    }

    [Fact]
    public async Task ADragThatOpenedNothing_DoesNotCloseAnOpenPillEither()
    {
        var orb = await RecordingAwayAsync();
        orb.PointerPressed();
        orb.PointerReleased();
        Assert.True(orb.IsPillOpen);

        orb.PointerPressed();
        orb.PointerMoved(0, 20);
        orb.PointerReleased();

        Assert.True(orb.IsPillOpen);
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(3, 0, false)]
    [InlineData(0, -3.9, false)]
    [InlineData(4, 0, true)]
    [InlineData(3, 3, true)]
    public async Task AMoveOfLessThanFourDips_IsStillAClick(double dx, double dy, bool drag)
    {
        var orb = await RecordingAwayAsync();

        orb.PointerPressed();
        var dragging = orb.PointerMoved(dx, dy);
        var wasDrag = orb.PointerReleased();

        Assert.Equal(drag, dragging);
        Assert.Equal(drag, wasDrag);
        Assert.Equal(!drag, orb.IsPillOpen);
    }

    [Fact]
    public async Task OnceDragging_ComingBackToTheStart_IsStillADrag()
    {
        var orb = await RecordingAwayAsync();

        orb.PointerPressed();
        orb.PointerMoved(40, 0);
        orb.PointerMoved(0, 0);

        Assert.True(orb.PointerReleased());
        Assert.False(orb.IsPillOpen);
    }

    [Fact]
    public async Task ACancelledPress_IsNeitherAClickNorADrag()
    {
        var orb = await RecordingAwayAsync();

        orb.PointerPressed();
        orb.PointerCancelled();

        Assert.False(orb.PointerReleased());
        Assert.False(orb.IsPillOpen);
    }

    [Fact]
    public async Task AReleaseWithoutAPress_DoesNothing()
    {
        var orb = await RecordingAwayAsync();

        Assert.False(orb.PointerReleased());
        Assert.False(orb.IsPillOpen);
        Assert.False(orb.PointerMoved(50, 50));
    }

    [Fact]
    public async Task ThePill_ClosesWhenTheRecordingScreenIsBack_AndWhenTheRecordingEnds()
    {
        var orb = await RecordingAwayAsync();
        orb.PointerPressed();
        orb.PointerReleased();
        Assert.True(orb.IsPillOpen);

        orb.IsRecorderInView = true;
        Assert.False(orb.IsPillOpen);

        orb.IsRecorderInView = false;
        orb.PointerPressed();
        orb.PointerReleased();
        Assert.True(orb.IsPillOpen);

        await _recorder.StopAsync(Ct);
        Assert.False(orb.IsPillOpen);
    }

    // ---- the buttons ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheButtons_RunTheirActions_StopAndOpenCloseThePill()
    {
        var orb = await RecordingAwayAsync();
        orb.PointerPressed();
        orb.PointerReleased();

        await orb.PauseResumeCommand.ExecuteAsync(null);
        Assert.Equal(["pause"], _calls);
        Assert.True(orb.IsPillOpen);

        orb.OpenCommand.Execute(null);
        Assert.Equal(["pause", "open"], _calls);
        Assert.False(orb.IsPillOpen);

        orb.PointerPressed();
        orb.PointerReleased();
        await orb.StopCommand.ExecuteAsync(null);
        Assert.Equal(["pause", "open", "stop"], _calls);
        Assert.False(orb.IsPillOpen);
    }

    [Fact]
    public void TheButtonsOfARecording_AreDisabledWhileIdle_AndOpenAlwaysWorks()
    {
        var orb = Create();

        Assert.False(orb.MarkCommand.CanExecute(null));
        Assert.False(orb.PauseResumeCommand.CanExecute(null));
        Assert.False(orb.StopCommand.CanExecute(null));
        Assert.True(orb.OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheButtons_BecomeAvailableWithTheRecording_AndAreRaisedAgainWhenItEnds()
    {
        var orb = Create();
        var changed = new List<string>();
        orb.MarkCommand.CanExecuteChanged += (_, _) => changed.Add("mark");
        orb.StopCommand.CanExecuteChanged += (_, _) => changed.Add("stop");

        await _recorder.StartAsync(new RecordingOptions(), Ct);
        Assert.True(orb.MarkCommand.CanExecute(null));
        Assert.True(orb.PauseResumeCommand.CanExecute(null));
        Assert.True(orb.StopCommand.CanExecute(null));

        await _recorder.PauseAsync(Ct);
        Assert.True(orb.MarkCommand.CanExecute(null));

        await _recorder.StopAsync(Ct);
        Assert.False(orb.StopCommand.CanExecute(null));
        Assert.Contains("mark", changed);
        Assert.Contains("stop", changed);
    }

    [Fact]
    public async Task WithoutMarks_TheMarkButtonIsDisabled()
    {
        var orb = Create(withMarks: false);
        await _recorder.StartAsync(new RecordingOptions(), Ct);

        Assert.False(orb.MarkCommand.CanExecute(null));
    }

    [Fact]
    public void TheLabels_AreTheStringsOfTheRecordingScreen()
    {
        var orb = Create();

        Assert.Equal("Mark moment", orb.MarkLabel);
        Assert.Equal("Stop and transcribe", orb.StopLabel);
        Assert.Equal("Open Murmur", orb.OpenLabel);
        Assert.Equal("Pause", orb.PauseLabel);
    }

    [Fact]
    public void TheLabels_AreUkrainianWhenTheLanguageIs()
    {
        using var ukrainian = UiCulture.Override("uk");
        var orb = Create();

        Assert.Equal("Відкрити Murmur", orb.OpenLabel);
        Assert.Equal("Позначити момент", orb.MarkLabel);
    }

    // ---- Mark and its chip -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Mark_AddsAMarkAtTheElapsedTime_AndShowsTheChipForTwoSeconds()
    {
        var orb = await RecordingAwayAsync();
        _recorder.Elapsed = TimeSpan.FromSeconds(761);

        orb.MarkCommand.Execute(null);

        Assert.Equal([TimeSpan.FromSeconds(761)], _marks.Current);
        Assert.True(orb.IsChipShown);
        Assert.Equal("Marked", orb.ChipLabel);
        Assert.Equal("12:41", orb.ChipTime);

        _time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.True(orb.IsChipShown);

        _time.Advance(TimeSpan.FromMilliseconds(2));
        Assert.False(orb.IsChipShown);
    }

    [Fact]
    public async Task AMarkFromTheHotkey_ShowsTheChipToo()
    {
        var orb = await RecordingAwayAsync();
        _recorder.Elapsed = TimeSpan.FromSeconds(3725);

        _marks.TryMark(out _);

        Assert.True(orb.IsChipShown);
        Assert.Equal("1:02:05", orb.ChipTime);
    }

    [Fact]
    public async Task MarkingAgain_RestartsTheTwoSeconds_AndShowsTheNewTime()
    {
        var orb = await RecordingAwayAsync();
        _recorder.Elapsed = TimeSpan.FromSeconds(10);
        orb.MarkCommand.Execute(null);
        _time.Advance(TimeSpan.FromMilliseconds(1500));

        _recorder.Elapsed = TimeSpan.FromSeconds(12);
        orb.MarkCommand.Execute(null);
        _time.Advance(TimeSpan.FromMilliseconds(1500));

        Assert.True(orb.IsChipShown);
        Assert.Equal("00:12", orb.ChipTime);

        _time.Advance(TimeSpan.FromMilliseconds(600));
        Assert.False(orb.IsChipShown);
    }

    [Fact]
    public async Task AMarkMadeWhilePaused_ShowsTheChip()
    {
        var orb = await RecordingAwayAsync();
        _recorder.Elapsed = TimeSpan.FromSeconds(30);
        await _recorder.PauseAsync(Ct);

        orb.MarkCommand.Execute(null);

        Assert.True(orb.IsChipShown);
        Assert.Equal("00:30", orb.ChipTime);
    }

    [Fact]
    public async Task TheChip_DoesNotOutliveTheRecording()
    {
        var orb = await RecordingAwayAsync();
        orb.MarkCommand.Execute(null);
        Assert.True(orb.IsChipShown);

        await _recorder.StopAsync(Ct);

        Assert.False(orb.IsChipShown);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public async Task AMarkThatWasNotMade_ShowsNoChip()
    {
        var orb = await RecordingAwayAsync();
        await _recorder.StopAsync(Ct);

        orb.MarkCommand.Execute(null);
        Assert.False(_marks.TryMark(out _));

        Assert.False(orb.IsChipShown);
        Assert.Empty(_marks.Current);
    }

    [Fact]
    public async Task MarksThatCouldNotBeSaved_ShowAShortErrorChipForTwoSeconds()
    {
        var orb = await RecordingAwayAsync();
        _bookmarks.Failure = new IOException("database is locked");
        orb.MarkCommand.Execute(null);
        await _recorder.StopAsync(Ct);
        await _marks.PendingFlush;

        Assert.True(orb.IsChipShown);
        Assert.Equal("Marks could not be saved", orb.ChipLabel);
        Assert.False(orb.HasChipTime);

        _time.Advance(TimeSpan.FromMilliseconds(2100));
        Assert.False(orb.IsChipShown);

        // The next mark is a normal chip again.
        await _recorder.StartAsync(new RecordingOptions(), Ct);
        _recorder.Elapsed = TimeSpan.FromSeconds(5);
        _marks.TryMark(out _);
        Assert.Equal("Marked", orb.ChipLabel);
        Assert.True(orb.HasChipTime);
    }

    // ---- the position ----------------------------------------------------------------------------------------------------------

    private static readonly WorkArea Primary = new(0, 0, 1920, 1040, 1.0);

    [Fact]
    public void WithoutASavedPosition_TheOrbSitsInTheBottomRightOfThePrimaryWorkArea_24DipsFromTheEdges()
    {
        var orb = Create();

        var placement = orb.ResolvePlacement([Primary]);

        Assert.Equal(new OrbPlacement(1920 - 24 - 44, 1040 - 24 - 44, Corrected: false), placement);
    }

    [Fact]
    public void TheDefaultSpot_ScalesWithTheScreen()
    {
        var orb = Create();
        var scaled = new WorkArea(0, 0, 2880, 1560, 1.5);

        var placement = orb.ResolvePlacement([scaled]);

        Assert.Equal(new OrbPlacement(2880 - 36 - 66, 1560 - 36 - 66, Corrected: false), placement);
    }

    [Fact]
    public async Task ADraggedPosition_IsSaved_AndRestoredByTheNextOrb()
    {
        var orb = Create();

        var error = await orb.SavePositionAsync(1200, 300, [Primary]);

        Assert.Null(error);
        Assert.Equal(new FloatingOrbPosition(1200, 300), _settings.Current.FloatingOrbPosition);
        var next = Create();
        Assert.Equal(new OrbPlacement(1200, 300, Corrected: false), next.ResolvePlacement([Primary]));
    }

    [Fact]
    public async Task APositionOnAScaledSecondScreen_IsSavedInDipsOfThePrimaryScreen()
    {
        var orb = Create();
        var primary = new WorkArea(0, 0, 1920, 1040, 1.0);
        var right = new WorkArea(1920, 0, 2880, 1560, 1.5);

        await orb.SavePositionAsync(1920 + 300, 150, [primary, right]);

        Assert.Equal(new FloatingOrbPosition(2220, 150), _settings.Current.FloatingOrbPosition);
        Assert.Equal(new OrbPlacement(2220, 150, Corrected: false), orb.ResolvePlacement([primary, right]));
    }

    [Fact]
    public async Task APositionOnAMonitorThatWasUnplugged_IsMovedIntoTheNearestWorkArea()
    {
        var orb = Create();
        var right = new WorkArea(1920, 0, 1920, 1040, 1.0);
        await orb.SavePositionAsync(3500, 400, [Primary, right]);

        // The right monitor is gone: only the primary one is left.
        var placement = orb.ResolvePlacement([Primary]);

        Assert.Equal(new OrbPlacement(1920 - 44, 400, Corrected: true), placement);
        Assert.Equal(new FloatingOrbPosition(3500, 400), _settings.Current.FloatingOrbPosition);
    }

    [Fact]
    public async Task TheSavedSpot_ComesBackWhenTheMonitorIsPluggedInAgain()
    {
        var orb = Create();
        var right = new WorkArea(1920, 0, 1920, 1040, 1.0);
        await orb.SavePositionAsync(3500, 400, [Primary, right]);
        Assert.True(orb.ResolvePlacement([Primary])!.Value.Corrected);

        var placement = orb.ResolvePlacement([Primary, right]);

        Assert.Equal(new OrbPlacement(3500, 400, Corrected: false), placement);
    }

    [Theory]
    [InlineData(-500, -300, 0, 0)]
    [InlineData(5000, 5000, 1920 - 44, 1040 - 44)]
    [InlineData(-40, 500, 0, 500)]
    public async Task AnOrbOffTheScreen_IsPulledBackWhole_IntoTheWorkArea(double savedX, double savedY, int x, int y)
    {
        _settings.SetCurrentSilently(new AppSettings { FloatingOrbPosition = new FloatingOrbPosition(savedX, savedY) });
        var orb = Create();

        var placement = orb.ResolvePlacement([Primary]);

        Assert.Equal(new OrbPlacement(x, y, Corrected: true), placement);
        await Task.CompletedTask;
    }

    [Fact]
    public void AnOrbHalfOffTheEdge_IsPulledBackWhole()
    {
        _settings.SetCurrentSilently(new AppSettings { FloatingOrbPosition = new FloatingOrbPosition(1895, 500) });
        var orb = Create();

        var placement = orb.ResolvePlacement([Primary]);

        Assert.Equal(new OrbPlacement(1920 - 44, 500, Corrected: true), placement);
    }

    [Fact]
    public void TheOrbThisRunPutDown_WinsOverTheSavedSpot_AndIsCheckedLikeIt()
    {
        _settings.SetCurrentSilently(new AppSettings { FloatingOrbPosition = new FloatingOrbPosition(100, 100) });
        var orb = Create();

        Assert.Equal(new OrbPlacement(700, 300, Corrected: false), orb.ResolvePlacement([Primary], new FloatingOrbPosition(700, 300)));
        Assert.Equal(new OrbPlacement(1920 - 44, 300, Corrected: true), orb.ResolvePlacement([Primary], new FloatingOrbPosition(5000, 300)));
    }

    [Fact]
    public void WithoutAPrimaryScreen_NothingSavedIsUsed_TheDefaultSpotStays()
    {
        _settings.SetCurrentSilently(new AppSettings { FloatingOrbPosition = new FloatingOrbPosition(100, 100) });
        var orb = Create();

        var placement = orb.ResolvePlacement([Primary], new FloatingOrbPosition(700, 300), primaryKnown: false);

        Assert.Equal(new OrbPlacement(1920 - 24 - 44, 1040 - 24 - 44, Corrected: false), placement);
    }

    [Fact]
    public void AnOrbUnderTheTaskbar_IsMovedAboveIt()
    {
        // The work area ends at 1040 (the taskbar is below).
        _settings.SetCurrentSilently(new AppSettings { FloatingOrbPosition = new FloatingOrbPosition(800, 1060) });
        var orb = Create();

        var placement = orb.ResolvePlacement([Primary]);

        Assert.Equal(new OrbPlacement(800, 1040 - 44, Corrected: true), placement);
    }

    [Fact]
    public void WithoutScreens_ThereIsNoPlacement()
    {
        var orb = Create();

        Assert.Null(orb.ResolvePlacement([]));
    }

    [Fact]
    public async Task WithoutASettingsStore_TheOrbJustForgetsWhereItWas()
    {
        var orb = Create(withSettings: false);

        var error = await orb.SavePositionAsync(100, 100, [Primary]);

        Assert.Null(error);
        Assert.Equal(new OrbPlacement(1852, 972, Corrected: false), orb.ResolvePlacement([Primary]));
    }

    [Fact]
    public async Task AFailedSave_IsReported_NotThrown()
    {
        _settings.SaveFailure = new IOException("disk is full");
        var orb = Create();

        var error = await orb.SavePositionAsync(100, 100, [Primary]);

        Assert.NotNull(error);
        Assert.Contains("disk is full", error, StringComparison.Ordinal);
    }

    // ---- lifetime ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Disposed_TheOrbListensToNothing()
    {
        var orb = Create();
        orb.Dispose();

        await _recorder.StartAsync(new RecordingOptions(), Ct);
        _marks.TryMark(out _);
        _motion.ReduceMotion = true;
        _motion.Raise();

        Assert.Equal(RecorderState.Idle, orb.State);
        Assert.False(orb.IsChipShown);
        Assert.False(orb.ReduceMotion);
        Assert.False(_motion.HasSubscribers);
    }
}
