using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

/// <summary>The Recording screen of the canvas: pause and resume, the editable title, the Tracks card, the copied message.</summary>
public sealed class RecordingPauseViewModelTests : IDisposable
{
    // The texts asserted below are the canvas's English ones.
    private readonly IDisposable _english = UiCulture.Override("en");

    private readonly FakeRecorder _recorder = new();
    private readonly FakeCapture _capture = new();
    private readonly TitleStore _store = new();
    private readonly TestTime _time = new(UiHarness.Now);

    public void Dispose() => _english.Dispose();

    private RecordingViewModel Create() => new(_recorder, _capture, _store, action => action(), time: _time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A store that keeps the sessions the fake recorder starts, so a rename has a row to change.</summary>
    private sealed class TitleStore : ISessionStore
    {
        private readonly Dictionary<SessionId, SessionRecord> _rows = [];

        public Exception? UpdateFailure { get; set; }

        public void Seed(SessionRecord record) => _rows[record.Id] = record;

        public SessionRecord? Get(SessionId id) => _rows.GetValueOrDefault(id);

        public ValueTask<SessionRecord> CreateSessionAsync(string title, DateTimeOffset startedAt, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask UpdateSessionAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            if (UpdateFailure is { } failure)
            {
                throw failure;
            }

            _rows[session.Id] = session;
            return ValueTask.CompletedTask;
        }

        public ValueTask<SessionRecord?> GetSessionAsync(SessionId id, CancellationToken cancellationToken) => ValueTask.FromResult(_rows.GetValueOrDefault(id));

        public ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(SessionQuery query, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<SessionRecord>>([.. _rows.Values]);

        public ValueTask DeleteSessionAsync(SessionId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public SessionFiles GetFiles(SessionId id) => new(Path.Combine(Path.GetTempPath(), "murmur-title-store", id.ToString()));
    }

    // ---- pause and resume -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePauseButton_IsOnlyAvailableWhileRecordingOrPaused()
    {
        var vm = Create();
        Assert.False(vm.CanPauseResume);
        Assert.False(vm.PauseResumeCommand.CanExecute(null));

        await vm.StartStopAsync();
        Assert.True(vm.CanPauseResume);
        Assert.True(vm.PauseResumeCommand.CanExecute(null));

        await vm.PauseResumeCommand.ExecuteAsync(null);
        Assert.True(vm.CanPauseResume);

        await vm.StartStopAsync();
        Assert.False(vm.CanPauseResume);
    }

    [Fact]
    public async Task Pause_ShowsThePausedState_Resume_TakesItBack()
    {
        var vm = Create();
        await vm.StartStopAsync();
        Assert.Equal("Pause", vm.PauseLabel);
        Assert.Equal("Auto-saved to disk every few seconds", vm.TimerNote);
        Assert.True(vm.OrbActive);
        Assert.False(vm.IsPaused);

        await vm.PauseResumeCommand.ExecuteAsync(null);

        Assert.Equal(RecorderState.Paused, vm.State);
        Assert.True(vm.IsPaused);
        Assert.True(vm.IsRunning);
        Assert.False(vm.IsRecording);
        Assert.Equal("Resume", vm.PauseLabel);
        Assert.Equal("Paused — nothing is recorded until you resume", vm.TimerNote);
        Assert.False(vm.OrbActive); // the orb rests
        Assert.Equal("Stop and transcribe", vm.StartStopLabel);
        Assert.False(vm.CanGoBack);
        Assert.Equal(1, _recorder.Pauses);

        await vm.PauseResumeCommand.ExecuteAsync(null);

        Assert.Equal(RecorderState.Recording, vm.State);
        Assert.Equal("Pause", vm.PauseLabel);
        Assert.True(vm.OrbActive);
        Assert.Equal(1, _recorder.Resumes);
    }

    [Fact]
    public async Task WhilePaused_TheTimerStandsStillAndTheTracksSayPaused()
    {
        var vm = Create();
        await vm.StartStopAsync();
        _recorder.Elapsed = TimeSpan.FromSeconds(83);
        _recorder.Levels = new AudioLevels(0.126f, 0.063f);
        vm.Refresh();
        Assert.Equal("00:01:23", vm.TimerText);
        Assert.Equal("−18 dB", vm.MicrophoneTrack.LevelText);

        await vm.PauseResumeCommand.ExecuteAsync(null);
        vm.Refresh();

        Assert.Equal("00:01:23", vm.TimerText);
        Assert.Equal("Paused", vm.MicrophoneTrack.LevelText);
        Assert.Equal("Paused", vm.SystemTrack.LevelText);
        Assert.Equal(RecordingTrackState.Paused, vm.MicrophoneTrack.State);
    }

    [Fact]
    public async Task Pause_WhenTheRecorderFails_ShowsTheErrorAndStaysRecording()
    {
        var vm = Create();
        await vm.StartStopAsync();
        _recorder.PauseFailure = new InvalidOperationException("the writer is gone");

        await vm.PauseResumeCommand.ExecuteAsync(null);

        Assert.Equal(RecorderState.Recording, vm.State);
        Assert.Equal("Couldn’t pause the recording: the writer is gone", vm.Error);
        Assert.True(vm.CanPauseResume); // can be tried again
    }

    [Fact]
    public async Task Resume_WhenTheRecorderFails_ShowsTheErrorAndStaysPaused()
    {
        var vm = Create();
        await vm.StartStopAsync();
        await vm.PauseResumeCommand.ExecuteAsync(null);
        _recorder.PauseFailure = new InvalidOperationException("the writer is gone");

        await vm.PauseResumeCommand.ExecuteAsync(null);

        Assert.Equal(RecorderState.Paused, vm.State);
        Assert.Equal("Couldn’t resume the recording: the writer is gone", vm.Error);
    }

    [Fact]
    public async Task ThePauseButton_IsBusyWhileThePauseIsBeingApplied_SoItCannotBePressedTwice()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create();
        await vm.StartStopAsync();
        _recorder.PauseGate = gate.Task;

        var first = vm.PauseResumeCommand.ExecuteAsync(null);
        Assert.False(vm.CanPauseResume);
        await vm.PauseResumeAsync(); // a second press while the first one is pending does nothing
        gate.SetResult();
        await first;

        Assert.Equal(1, _recorder.Pauses);
        Assert.Equal(RecorderState.Paused, vm.State);
        Assert.True(vm.CanPauseResume);
    }

    [Fact]
    public async Task Stop_WhilePaused_StopsTheRecording()
    {
        var vm = Create();
        SessionRecord? stopped = null;
        vm.Stopped += s => stopped = s;
        await vm.StartStopAsync();
        await vm.PauseResumeCommand.ExecuteAsync(null);

        await vm.StartStopCommand.ExecuteAsync(null);

        Assert.Equal(1, _recorder.Stops);
        Assert.Equal(RecorderState.Idle, vm.State);
        Assert.NotNull(stopped);
        Assert.Equal("00:00:00", vm.TimerText);
        Assert.False(vm.CanPauseResume);
    }

    [Fact]
    public async Task ThePauseHotkey_TogglesPause_OnlyWhileRecording()
    {
        var vm = Create();
        await vm.TogglePauseFromHotkeyAsync();
        Assert.Equal(0, _recorder.Pauses);

        await vm.StartStopAsync();
        await vm.TogglePauseFromHotkeyAsync();
        Assert.Equal(RecorderState.Paused, vm.State);
        await vm.TogglePauseFromHotkeyAsync();
        Assert.Equal(RecorderState.Recording, vm.State);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _recorder.StopGate = gate.Task;
        var stopping = vm.StopAsync();
        await vm.TogglePauseFromHotkeyAsync(); // stopping: ignored
        Assert.Equal(1, _recorder.Resumes);
        Assert.Equal(1, _recorder.Pauses);
        gate.SetResult();
        await stopping;
    }

    [Fact]
    public async Task TheRecordHotkey_StopsAPausedRecording()
    {
        var vm = Create();
        await vm.StartStopAsync();
        await vm.PauseResumeCommand.ExecuteAsync(null);

        await vm.ToggleRecordingFromHotkeyAsync();

        Assert.Equal(RecorderState.Idle, vm.State);
        Assert.Equal(1, _recorder.Stops);
    }

    [Fact]
    public async Task TheScreenPill_WorksWhilePaused_AndThePillTellsWhatTheResumeStarts()
    {
        var vm = Create();
        vm.Screen = true;
        await vm.StartStopAsync();
        await vm.PauseResumeCommand.ExecuteAsync(null);
        Assert.True(vm.CanToggleScreen);

        vm.Screen = false;
        await vm.PendingScreenChange;
        Assert.Equal([false], _recorder.ScreenCalls);
        await vm.ToggleScreenFromHotkeyAsync(); // the screen hotkey flips the pill while paused
        Assert.True(vm.Screen);
        Assert.Equal([false, true], _recorder.ScreenCalls);

        await vm.PauseResumeCommand.ExecuteAsync(null);
        Assert.True(_recorder.IsScreenRecording);
        Assert.True(vm.Screen); // the pill was not switched off behind the user's back
        vm.Refresh();
        Assert.True(vm.Screen);
    }

    [Fact]
    public async Task APausedRecording_StoppedByTheRecorderItself_IsReported()
    {
        var vm = Create();
        await vm.StartStopAsync();
        await vm.PauseResumeCommand.ExecuteAsync(null);

        _recorder.FailBySelf();

        Assert.Equal(RecorderState.Idle, vm.State);
        Assert.False(vm.CanPauseResume);
    }

    // ---- the title ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheTitleField_StartsWithTheCanvasDefaultTitle()
    {
        var vm = Create();

        Assert.Equal("Untitled — Oct 18, 15:00", vm.Title);
    }

    [Fact]
    public async Task ATitleTypedBeforeTheStart_IsTheTitleOfTheRecording()
    {
        var vm = Create();
        vm.Title = "  Budget review  ";
        await vm.CommitTitleAsync();
        Assert.Equal("Budget review", vm.Title);

        await vm.StartStopAsync();

        Assert.Equal("Budget review", Assert.Single(_recorder.Starts).Title);
        Assert.Equal("Budget review", vm.Title);
    }

    [Fact]
    public async Task AnUntouchedTitle_IsRebuiltAtTheStart_NotKeptFromWhenTheScreenWasOpened()
    {
        var vm = Create();
        _time.Advance(TimeSpan.FromMinutes(5));

        await vm.StartStopAsync();

        Assert.Equal("Untitled — Oct 18, 15:05", Assert.Single(_recorder.Starts).Title);
        Assert.Equal("Untitled — Oct 18, 15:05", vm.Title);
    }

    [Fact]
    public async Task EditingTheTitleWhileRecording_RenamesTheSession()
    {
        var vm = Create();
        _recorder.AfterStart = () => _store.Seed(_recorder.Current!);
        await vm.StartStopAsync();
        var id = _recorder.Current!.Id;

        vm.Title = "Client call";
        await vm.CommitTitleAsync();

        Assert.Equal("Client call", _store.Get(id)!.Title);
        Assert.Equal("Client call", vm.Title);
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task TheTitle_CanBeRenamedWhilePausedToo()
    {
        var vm = Create();
        _recorder.AfterStart = () => _store.Seed(_recorder.Current!);
        await vm.StartStopAsync();
        await vm.PauseResumeCommand.ExecuteAsync(null);
        var id = _recorder.Current!.Id;

        vm.Title = "On hold";
        await vm.CommitTitleAsync();

        Assert.Equal("On hold", _store.Get(id)!.Title);
    }

    [Fact]
    public async Task Esc_GivesBackTheTitle_AndAnEmptyTitleIsRefused()
    {
        var vm = Create();
        _recorder.AfterStart = () => _store.Seed(_recorder.Current!);
        await vm.StartStopAsync();
        var id = _recorder.Current!.Id;
        var original = vm.Title;

        vm.Title = "Something else";
        vm.CancelTitle();
        await vm.CommitTitleAsync();
        Assert.Equal(original, vm.Title);
        Assert.Equal(original, _store.Get(id)!.Title);

        vm.Title = "   ";
        await vm.CommitTitleAsync();
        Assert.Equal(original, vm.Title);
        Assert.Equal(original, _store.Get(id)!.Title);
    }

    [Fact]
    public async Task ARenameThatFails_ShowsTheErrorAndKeepsTheOldTitle()
    {
        var vm = Create();
        _recorder.AfterStart = () => _store.Seed(_recorder.Current!);
        await vm.StartStopAsync();
        var original = vm.Title;
        _store.UpdateFailure = new IOException("disk is full");

        vm.Title = "New name";
        await vm.CommitTitleAsync();

        Assert.Equal(original, vm.Title);
        Assert.Equal("Couldn’t rename the recording: disk is full", vm.Error);
    }

    // ---- the Tracks card ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Tracks_ShowTheLevelInDecibels_TheDeviceAndGrowTheStrip()
    {
        var vm = Create();
        await vm.StartStopAsync();
        _recorder.Levels = new AudioLevels(0.126f, 0.063f);

        vm.Refresh();

        Assert.Equal("You · microphone", vm.MicrophoneTrack.Label);
        Assert.Equal("Others · system audio", vm.SystemTrack.Label);
        Assert.Equal("−18 dB", vm.MicrophoneTrack.LevelText);
        Assert.Equal("−24 dB", vm.SystemTrack.LevelText);
        Assert.Equal("Built-in microphone", vm.MicrophoneTrack.Detail);
        Assert.Equal("Speakers · loopback", vm.SystemTrack.Detail);
        Assert.Equal(RecordingTrackState.Active, vm.MicrophoneTrack.State);
        Assert.False(vm.MicrophoneTrack.IsIdle);

        // The newest sample is the last one of the strip; every refresh moves the strip on by one.
        var strip = vm.MicrophoneTrack.History.Values;
        Assert.Equal(26, strip.Length);
        Assert.Equal(0.7, strip[^1], 0.01); // −18 dB on a −60..0 dB scale
        _recorder.Levels = new AudioLevels(0.5f, 0f);
        vm.Refresh();
        Assert.Equal(0.7, vm.MicrophoneTrack.History.Values[^2], 0.01);
        Assert.Equal(0.9, vm.MicrophoneTrack.History.Values[^1], 0.02);
        Assert.Equal("−6 dB", vm.MicrophoneTrack.LevelText);
        Assert.Equal("−60 dB", vm.SystemTrack.LevelText);
    }

    [Fact]
    public async Task Tracks_TheDeviceChosenForTheRecording_IsTheOneNamed()
    {
        var vm = Create();
        vm.SelectedMicrophone = _capture.Microphones[1];
        vm.SelectedSystemDevice = _capture.Outputs[1];

        await vm.StartStopAsync();
        vm.Refresh();

        Assert.Equal("USB headset", vm.MicrophoneTrack.Detail);
        Assert.Equal("HDMI monitor · loopback", vm.SystemTrack.Detail);
    }

    [Fact]
    public async Task ATrackThatIsOff_SaysSoInTheSameLayout()
    {
        var vm = Create();
        vm.Microphone = false;

        await vm.StartStopAsync();
        vm.Refresh();

        Assert.Equal(RecordingTrackState.Off, vm.MicrophoneTrack.State);
        Assert.True(vm.MicrophoneTrack.IsIdle);
        Assert.Equal("Off", vm.MicrophoneTrack.LevelText);
        Assert.Equal("Not recorded in this session", vm.MicrophoneTrack.Detail);
        Assert.Equal(RecordingTrackState.Active, vm.SystemTrack.State);
    }

    [Fact]
    public async Task ADeniedMicrophone_ShowsBlockedWithTheReason()
    {
        _recorder.StartWarning = SessionNotes.Format(SessionNotes.MicrophoneDenied);
        var vm = Create();

        await vm.StartStopAsync();
        vm.Refresh();

        Assert.Equal(RecordingTrackState.Denied, vm.MicrophoneTrack.State);
        Assert.Equal("Blocked", vm.MicrophoneTrack.LevelText);
        Assert.Equal(Strings.Note_MicDenied, vm.MicrophoneTrack.Detail);
        Assert.Equal(RecordingTrackState.Active, vm.SystemTrack.State);
    }

    [Fact]
    public async Task AMicrophoneThatCouldNotStart_ShowsUnavailable()
    {
        _recorder.StartWarning = SessionNotes.Format(SessionNotes.MicrophoneUnavailable, "device busy");
        var vm = Create();

        await vm.StartStopAsync();
        vm.Refresh();

        Assert.Equal(RecordingTrackState.Unavailable, vm.MicrophoneTrack.State);
        Assert.Equal("Unavailable", vm.MicrophoneTrack.LevelText);
        Assert.Equal("The device could not be started", vm.MicrophoneTrack.Detail);
    }

    [Fact]
    public async Task WhenTheRecordingEnds_TheTracksAreEmptyAgain()
    {
        var vm = Create();
        await vm.StartStopAsync();
        _recorder.Levels = new AudioLevels(0.5f, 0.5f);
        vm.Refresh();

        await vm.StartStopAsync();

        Assert.Equal(string.Empty, vm.MicrophoneTrack.LevelText);
        Assert.All(vm.MicrophoneTrack.History.Values.ToArray(), v => Assert.Equal(0f, v));
    }

    [Theory]
    [InlineData(0.0f, 0.0)]
    [InlineData(1.0f, 1.0)]
    [InlineData(0.1f, 0.667)]
    [InlineData(0.001f, 0.0)]
    [InlineData(0.0001f, 0.0)]
    [InlineData(float.NaN, 0.0)]
    [InlineData(2.0f, 1.0)]
    public void TheLevelHistory_NormalisesPeaksOnAMinusSixtyToZeroDecibelScale(float peak, double expected) =>
        Assert.Equal(expected, Controls.LevelHistory.Normalize(peak), 0.001);

    [Theory]
    [InlineData(-18.0, "−18")]
    [InlineData(-18.4, "−18")]
    [InlineData(-18.5, "−19")]
    [InlineData(-0.2, "0")]
    [InlineData(0.0, "0")]
    [InlineData(-60.0, "−60")]
    public void Decibels_AreWholeNumbersWithTheTypographicMinus(double db, string expected) =>
        Assert.Equal(expected, RecordingTrackViewModel.FormatDb(db));

    // ---- the message for the other people ----------------------------------------------------------------------------------

    [Fact]
    public async Task CopyMessage_PutsTheNoticeOnTheClipboard_AndTheButtonReadsCopiedForTwoSeconds()
    {
        var vm = Create();
        var copied = new List<string>();
        vm.ClipboardWriter = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };
        Assert.Equal("Copy message", vm.CopyLabel);

        await vm.CopyMessageCommand.ExecuteAsync(null);

        Assert.Equal([Strings.Recording_MessageToCopy], copied);
        Assert.Contains("this meeting is being recorded", copied[0], StringComparison.Ordinal);
        Assert.Contains("ця зустріч записується", copied[0], StringComparison.Ordinal); // English and Ukrainian
        Assert.True(vm.MessageCopied);
        Assert.Equal("Copied", vm.CopyLabel);

        _time.Advance(TimeSpan.FromMilliseconds(1_900));
        Assert.Equal("Copied", vm.CopyLabel);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(vm.MessageCopied);
        Assert.Equal("Copy message", vm.CopyLabel);
    }

    [Fact]
    public async Task CopyingAgainBeforeItIsOver_RestartsTheTwoSeconds()
    {
        var vm = Create();
        vm.ClipboardWriter = _ => Task.CompletedTask;

        await vm.CopyMessageCommand.ExecuteAsync(null);
        _time.Advance(TimeSpan.FromMilliseconds(1_500));
        await vm.CopyMessageCommand.ExecuteAsync(null);
        _time.Advance(TimeSpan.FromMilliseconds(1_500));

        Assert.True(vm.MessageCopied); // the first timer must not have cleared the label

        _time.Advance(TimeSpan.FromMilliseconds(600));
        Assert.False(vm.MessageCopied);
    }

    [Fact]
    public async Task CopyMessage_WithoutAClipboard_DoesNothing()
    {
        var vm = Create();

        await vm.CopyMessageCommand.ExecuteAsync(null);

        Assert.False(vm.MessageCopied);
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task CopyMessage_WhenTheClipboardFails_ShowsTheErrorAndDoesNotSayCopied()
    {
        var vm = Create();
        vm.ClipboardWriter = _ => throw new InvalidOperationException("clipboard is locked");

        await vm.CopyMessageCommand.ExecuteAsync(null);

        Assert.False(vm.MessageCopied);
        Assert.Equal("clipboard is locked", vm.Error);
    }

    // ---- the screen chip ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheScreenChip_ReadsScreenOff_OrTheHeightOfTheVideo()
    {
        var display = new FakeDisplayCapture();
        var vm = new RecordingViewModel(_recorder, display, _store, action => action(), time: _time);
        Assert.Equal("Screen off", vm.ScreenLabel);

        vm.Screen = true;

        Assert.Equal("Screen · 1080p", vm.ScreenLabel);
    }

    [Fact]
    public void TheScreenChip_NeverPromisesMoreThanTheEncoderWrites()
    {
        var display = new FakeDisplayCapture();
        display.Displays.Clear();
        display.Displays.Add(new Murmur.Core.Capture.DisplayInfo("big", "Big", 3840, 2160, IsPrimary: true));
        var vm = new RecordingViewModel(_recorder, display, _store, action => action(), time: _time)
        {
            Screen = true,
        };

        Assert.Equal("Screen · 1080p", vm.ScreenLabel);
    }
}
