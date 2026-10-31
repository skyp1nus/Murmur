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
    private readonly FakeSessionStore _store = new();
    private readonly TestTime _time = new(UiHarness.Now);

    public void Dispose() => _english.Dispose();

    private RecordingViewModel Create() => new(_recorder, _capture, _store, action => action(), time: _time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        _recorder.AfterStart = () => _store.Add(_recorder.Current!);
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
        _recorder.AfterStart = () => _store.Add(_recorder.Current!);
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
        _recorder.AfterStart = () => _store.Add(_recorder.Current!);
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
        _recorder.AfterStart = () => _store.Add(_recorder.Current!);
        await vm.StartStopAsync();
        var original = vm.Title;
        _store.RenameFailure = new IOException("disk is full");

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
            return Task.FromResult(true);
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
        vm.ClipboardWriter = _ => Task.FromResult(true);

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
    public async Task CopyMessage_WhenTheClipboardFails_ShowsTheLocalizedErrorAndDoesNotSayCopied()
    {
        var vm = Create();
        vm.ClipboardWriter = _ => throw new InvalidOperationException("clipboard is locked");

        await vm.CopyMessageCommand.ExecuteAsync(null);

        Assert.False(vm.MessageCopied);
        Assert.Equal("Copy message", vm.CopyLabel);
        Assert.Equal("Couldn’t copy the message: clipboard is locked", vm.Error);
    }

    [Fact]
    public async Task CopyMessage_WhenTheWindowHasNoClipboard_SaysSoAndDoesNotSayCopied()
    {
        var vm = Create();
        vm.ClipboardWriter = _ => Task.FromResult(false);

        await vm.CopyMessageCommand.ExecuteAsync(null);

        Assert.False(vm.MessageCopied);
        Assert.Equal("Couldn’t copy the message: the clipboard is not available", vm.Error);
    }

    [Fact]
    public async Task CopyMessage_AfterAFailure_ClearsTheErrorOnSuccess()
    {
        var vm = Create();
        var ok = false;
        vm.ClipboardWriter = _ => ok ? Task.FromResult(true) : throw new InvalidOperationException("busy");
        await vm.CopyMessageCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Error);

        ok = true;
        await vm.CopyMessageCommand.ExecuteAsync(null);

        Assert.Null(vm.Error);
        Assert.True(vm.MessageCopied);
    }

    // ---- renaming races -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARenameStillBeingWritten_IsFinishedBeforeTheStop_AndTheStopDoesNotRollItBack()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create();
        _recorder.AfterStart = () => _store.Add(_recorder.Current!);
        await vm.StartStopAsync();
        var id = _recorder.Current!.Id;
        _store.RenameGate = gate.Task;

        vm.Title = "Slow rename";
        var rename = vm.CommitTitleAsync();
        var stop = vm.StopAsync();

        Assert.False(rename.IsCompleted);
        Assert.Equal(0, _recorder.Stops); // the stop waits for the write
        Assert.Equal(RecorderState.Recording, _recorder.State);

        gate.SetResult();
        await Task.WhenAll(rename, stop);

        Assert.Equal(1, _recorder.Stops);
        Assert.Equal("Slow rename", _store.Get(id)!.Title);
        Assert.Equal([(id, "Slow rename")], _store.Renames);
        Assert.Equal(RecorderState.Idle, vm.State);
    }

    [Fact]
    public async Task TheStoppedEvent_CarriesTheTitleTheUserChose()
    {
        var vm = Create();
        _recorder.AfterStart = () => _store.Add(_recorder.Current!);
        SessionRecord? stopped = null;
        vm.Stopped += s => stopped = s;
        await vm.StartStopAsync();

        vm.Title = "Chosen";
        await vm.CommitTitleAsync();
        await vm.StartStopAsync();

        Assert.Equal("Chosen", stopped!.Title);
    }

    [Fact]
    public async Task ARename_IsRefusedOnceTheStopHasBegun()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create();
        _recorder.AfterStart = () => _store.Add(_recorder.Current!);
        await vm.StartStopAsync();
        var original = vm.Title;
        _recorder.StopGate = gate.Task;
        var stop = vm.StopAsync();

        vm.Title = "Too late";
        await vm.CommitTitleAsync();

        Assert.Empty(_store.Renames);
        Assert.Equal(original, vm.Title);
        gate.SetResult();
        await stop;
    }

    [Fact]
    public async Task ALateAnswerForAnEarlierSession_DoesNotTouchTheNextOne()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create();
        _recorder.AfterStart = () => _store.Add(_recorder.Current!);
        await vm.StartStopAsync();
        _store.RenameGate = gate.Task;
        _store.RenameFailure = new IOException("disk is full");
        vm.Title = "Slow and failing";
        var rename = vm.CommitTitleAsync();

        // The recorder ends the first session by itself while the write is pending; the next recording starts.
        _recorder.FailBySelf();
        await vm.AutoStopReport;
        vm.Error = null;
        await vm.StartStopAsync();
        var second = vm.Title;
        Assert.NotEqual("Slow and failing", second);

        gate.SetResult();
        await rename;

        Assert.Null(vm.Error); // the failure belonged to the first session
        Assert.Equal(second, vm.Title);
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
