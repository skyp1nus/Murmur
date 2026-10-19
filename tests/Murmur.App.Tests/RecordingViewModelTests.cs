using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

public sealed class RecordingViewModelTests
{
    private readonly FakeRecorder _recorder = new();
    private readonly FakeCapture _capture = new();
    private readonly FakeSessionStore _store = new();

    private RecordingViewModel Create() => new(_recorder, _capture, _store, action => action());

    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(5, "00:00:05")]
    [InlineData(65, "00:01:05")]
    [InlineData(3_725, "01:02:05")]
    [InlineData(100 * 3_600 + 61, "100:01:01")]
    public void Duration_FormatsAsHoursMinutesSeconds(int seconds, string expected) =>
        Assert.Equal(expected, UiFormat.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Duration_TruncatesFractionsAndClampsNegative()
    {
        Assert.Equal("00:00:01", UiFormat.Duration(TimeSpan.FromSeconds(1.999)));
        Assert.Equal("00:00:00", UiFormat.Duration(TimeSpan.FromSeconds(-3)));
    }

    [Fact]
    public async Task OrbIsActive_OnlyWhileRecordingAndShown()
    {
        var vm = Create();
        Assert.False(vm.OrbActive);

        await vm.StartStopAsync();
        Assert.True(vm.OrbActive);

        vm.ShowOrb = false;
        Assert.False(vm.OrbActive);

        vm.ShowOrb = true;
        Assert.True(vm.OrbActive);
    }

    [Fact]
    public async Task StopAsync_WhileAStopIsInFlight_DoesNotStopTheRecorderTwice()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _recorder.StopGate = gate.Task;
        var vm = Create();
        await vm.StartStopAsync();

        var first = vm.StopAsync();
        var second = vm.StopAsync();
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, _recorder.Stops);
        Assert.Equal(RecorderState.Idle, vm.State);
    }

    [Fact]
    public async Task StartStopTwice_WithASynchronousRecorder_StopsTheRecorderBothTimes()
    {
        // The fake's StopAsync completes synchronously: the finished stop must not be remembered as "in flight".
        var vm = Create();

        await vm.StartStopAsync();
        await vm.StartStopAsync();
        await vm.StartStopAsync();
        await vm.StartStopAsync();

        Assert.Equal(2, _recorder.Starts.Count);
        Assert.Equal(2, _recorder.Stops);
        Assert.False(vm.IsRecording);
        Assert.Equal(RecorderState.Idle, vm.State);
        Assert.Equal(RecorderState.Idle, _recorder.State);
    }

    [Fact]
    public async Task StopAsync_AfterAnEarlierSynchronousStop_StopsTheNextRecording()
    {
        // The close guard of the window calls StopAsync directly.
        var vm = Create();
        await vm.StartStopAsync();
        await vm.StopAsync();
        await vm.StartStopAsync();
        Assert.True(vm.IsRecording);

        await vm.StopAsync();

        Assert.Equal(2, _recorder.Stops);
        Assert.False(vm.IsRecording);
        Assert.Equal(RecorderState.Idle, _recorder.State);
    }

    [Fact]
    public async Task StopFailure_ThenAnotherStop_CallsTheRecorderAgain()
    {
        var vm = Create();
        await vm.StartStopAsync();
        _recorder.StopFailure = new IOException("disk full");
        await vm.StartStopAsync();
        Assert.Equal("Не вдалося зупинити запис: disk full", vm.Error);
        Assert.True(vm.IsRecording);
        Assert.True(vm.StartStopCommand.CanExecute(null));

        _recorder.StopFailure = null;
        await vm.StartStopAsync();

        Assert.Equal(2, _recorder.Stops);
        Assert.False(vm.IsRecording);
        Assert.Equal(RecorderState.Idle, _recorder.State);
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task Stop_AfterTheRecorderStoppedBySelf_BeforeItsStateChangeArrived_ReportsTheAutomaticStop()
    {
        // Recorder events are posted to the UI thread; here they stay queued until drained by hand.
        var posted = new Queue<Action>();
        var vm = new RecordingViewModel(_recorder, _capture, _store, posted.Enqueue);
        await vm.StartStopAsync();
        Assert.True(vm.IsRecording);
        _store.Add(_recorder.Current! with { State = SessionState.Failed, Error = "Microphone unplugged." });

        _recorder.FailBySelf();
        await vm.StartStopAsync();
        await vm.AutoStopReport;

        Assert.Equal(0, _recorder.Stops);
        Assert.False(vm.IsRecording);
        Assert.Equal(RecorderState.Idle, vm.State);
        Assert.Equal("Запис зупинився: Microphone unplugged.", vm.Error);

        // The late state changes change nothing, and the next recording starts and stops normally.
        while (posted.TryDequeue(out var action))
        {
            action();
        }

        Assert.Equal("Запис зупинився: Microphone unplugged.", vm.Error);
        await vm.StartStopAsync();
        Assert.True(vm.IsRecording);
        await vm.StartStopAsync();

        Assert.Equal(1, _recorder.Stops);
        Assert.False(vm.IsRecording);
        Assert.Null(vm.Error);
    }

    [Fact]
    public void Defaults_MicAndSystemOnScreenOff_DefaultDevicesSelected()
    {
        var vm = Create();

        Assert.True(vm.Microphone);
        Assert.True(vm.SystemAudio);
        Assert.False(vm.Screen);
        Assert.Equal("mic-default", vm.SelectedMicrophone?.Id);
        Assert.Equal("out-default", vm.SelectedSystemDevice?.Id);
        Assert.Equal("00:00:00", vm.TimerText);
        Assert.Equal("Почати запис", vm.StartStopLabel);
        Assert.True(vm.CanToggleMicrophone);
        Assert.True(vm.CanToggleSystemAudio);
        Assert.True(vm.CanToggleScreen);
        Assert.True(vm.StartStopCommand.CanExecute(null));
    }

    [Fact]
    public async Task Start_PassesChosenSourcesAndDevicesToTheRecorder()
    {
        var vm = Create();
        vm.Screen = true;
        vm.SelectedMicrophone = _capture.Microphones[1];

        await vm.StartStopCommand.ExecuteAsync(null);

        var options = Assert.Single(_recorder.Starts);
        Assert.True(options.Microphone);
        Assert.True(options.SystemAudio);
        Assert.True(options.Screen);
        Assert.Equal("mic-usb", options.MicrophoneDeviceId);
        Assert.Null(options.SystemDeviceId);
        Assert.True(vm.IsRecording);
        Assert.Equal("Зупинити", vm.StartStopLabel);
    }

    [Fact]
    public async Task Start_WithMicrophoneOff_RecordsSystemAudioOnly()
    {
        var vm = Create();
        vm.Microphone = false;

        await vm.StartStopCommand.ExecuteAsync(null);

        var options = Assert.Single(_recorder.Starts);
        Assert.False(options.Microphone);
        Assert.True(options.SystemAudio);
    }

    [Fact]
    public void NoAudioSourceChosen_CannotStart_AndShowsHint()
    {
        var vm = Create();

        vm.Microphone = false;
        vm.SystemAudio = false;

        Assert.False(vm.StartStopCommand.CanExecute(null));
        Assert.Equal("Увімкни мікрофон або системний звук.", vm.SourceHint);
    }

    [Fact]
    public void MissingMicrophone_IsSkippedGracefully()
    {
        _capture.Microphones.Clear();

        var vm = Create();

        Assert.False(vm.MicrophoneAvailable);
        Assert.False(vm.Microphone);
        Assert.False(vm.CanToggleMicrophone);
        Assert.True(vm.StartStopCommand.CanExecute(null));
        Assert.Contains("лише системний звук", vm.SourceHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhileRecording_OnlyScreenToggleIsEnabled()
    {
        var vm = Create();

        await vm.StartStopCommand.ExecuteAsync(null);

        Assert.False(vm.CanToggleMicrophone);
        Assert.False(vm.CanToggleSystemAudio);
        Assert.False(vm.CanChangeDevices);
        Assert.False(vm.CanGoBack);
        Assert.True(vm.CanToggleScreen);
        Assert.False(vm.BackCommand.CanExecute(null));
    }

    [Fact]
    public async Task WhileRecording_ScreenToggleIsForwardedToTheRecorder()
    {
        var vm = Create();
        await vm.StartStopCommand.ExecuteAsync(null);

        vm.Screen = true;
        await vm.PendingScreenChange;
        vm.Screen = false;
        await vm.PendingScreenChange;

        Assert.Equal([true, false], _recorder.ScreenCalls);
    }

    [Fact]
    public void ScreenToggleBeforeRecording_DoesNotTouchTheRecorder()
    {
        var vm = Create();

        vm.Screen = true;

        Assert.Empty(_recorder.ScreenCalls);
    }

    [Fact]
    public async Task ScreenToggleFailure_ShowsErrorAndRevertsThePill()
    {
        var vm = Create();
        await vm.StartStopCommand.ExecuteAsync(null);
        _recorder.ScreenFailure = new InvalidOperationException("encoder missing");

        vm.Screen = true;
        await vm.PendingScreenChange;

        Assert.False(vm.Screen);
        Assert.Contains("encoder missing", vm.Error, StringComparison.Ordinal);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task AFatalScreenFault_TurnsThePillOffAndShowsANotice_WhileTheRecordingGoesOn()
    {
        var vm = Create();
        vm.Screen = true;
        await vm.StartStopAsync();
        Assert.True(vm.Screen);
        Assert.False(vm.HasNotice);

        _recorder.FailScreen();

        Assert.False(vm.Screen);
        Assert.True(vm.IsRecording);
        Assert.True(vm.HasNotice);
        Assert.Equal(Resources.Strings.Recording_ScreenStopped, vm.Notice);
        Assert.Empty(_recorder.ScreenCalls); // turning the pill off after the fault must not call the recorder
        Assert.Null(vm.Error);

        // The user can switch the screen on again.
        vm.Screen = true;
        await vm.PendingScreenChange;
        Assert.Equal([true], _recorder.ScreenCalls);
        Assert.True(_recorder.IsScreenRecording);
    }

    [Fact]
    public async Task ThePill_FollowsTheRecorder_WhenTheScreenStopsWithoutAWarning()
    {
        var vm = Create();
        vm.Screen = true;
        await vm.StartStopAsync();

        _recorder.IsScreenRecording = false;
        vm.Refresh();

        Assert.False(vm.Screen);
        Assert.Empty(_recorder.ScreenCalls);
    }

    [Fact]
    public async Task ANonFatalWarning_ShowsItsTextOnce_AndKeepsThePill()
    {
        const string text = "Запис екрана пропустив кадри: компʼютер не встигав.";
        var vm = Create();
        vm.Screen = true;
        await vm.StartStopAsync();

        _recorder.RaiseWarning(text);
        _recorder.RaiseWarning(text);
        _recorder.RaiseWarning("Під час запису екрана були збої.");

        Assert.True(vm.Screen);
        Assert.Equal(text + "\nПід час запису екрана були збої.", vm.Notice);
    }

    [Fact]
    public async Task TheNotice_IsClearedWhenARecordingStartsOrEnds()
    {
        var vm = Create();
        vm.Screen = true;
        await vm.StartStopAsync();
        _recorder.RaiseWarning("Під час запису екрана були збої.");
        Assert.True(vm.HasNotice);

        await vm.StartStopAsync();

        Assert.False(vm.HasNotice);
        _recorder.RaiseWarning("late"); // the recording is over: not a notice any more
        Assert.False(vm.HasNotice);

        vm.Screen = true;
        await vm.StartStopAsync();
        Assert.False(vm.HasNotice);
    }

    [Fact]
    public void ScreenUnavailable_DisablesThePill()
    {
        _capture.Screen = false;

        var vm = Create();

        Assert.False(vm.ScreenAvailable);
        Assert.False(vm.CanToggleScreen);
    }

    [Fact]
    public async Task Refresh_ShowsElapsedFromTheRecorder()
    {
        var vm = Create();
        await vm.StartStopCommand.ExecuteAsync(null);

        _recorder.Elapsed = TimeSpan.FromSeconds(3_725);
        vm.Refresh();

        Assert.Equal("01:02:05", vm.TimerText);
    }

    [Fact]
    public void Refresh_WhenIdle_LeavesTheTimerAlone()
    {
        var vm = Create();
        _recorder.Elapsed = TimeSpan.FromSeconds(30);

        vm.Refresh();

        Assert.Equal("00:00:00", vm.TimerText);
    }

    [Fact]
    public void Level_IsTheLouderOfMicrophoneAndSystem()
    {
        var vm = Create();

        _recorder.Levels = new AudioLevels(0.2f, 0.7f);
        Assert.Equal(0.7f, vm.Level);

        _recorder.Levels = new AudioLevels(0.9f, 0.1f);
        Assert.Equal(0.9f, vm.Level);
    }

    [Fact]
    public async Task Stop_RaisesStoppedWithTheSessionAndResetsTheTimer()
    {
        var vm = Create();
        SessionRecord? stopped = null;
        vm.Stopped += session => stopped = session;
        await vm.StartStopCommand.ExecuteAsync(null);
        _recorder.Elapsed = TimeSpan.FromSeconds(42);
        vm.Refresh();

        await vm.StartStopCommand.ExecuteAsync(null);

        Assert.NotNull(stopped);
        Assert.Equal(SessionState.Recorded, stopped.State);
        Assert.Equal(TimeSpan.FromSeconds(42), stopped.Duration);
        Assert.False(vm.IsRecording);
        Assert.Equal("00:00:00", vm.TimerText);
        Assert.Equal("Почати запис", vm.StartStopLabel);
        Assert.True(vm.CanToggleMicrophone);
    }

    [Fact]
    public async Task StartFailure_ShowsInlineErrorAndStaysIdle()
    {
        _recorder.StartFailure = new InvalidOperationException("no device");
        var vm = Create();

        await vm.StartStopCommand.ExecuteAsync(null);

        Assert.False(vm.IsRecording);
        Assert.Equal("Не вдалося почати запис: no device", vm.Error);
        Assert.True(vm.HasError);
        Assert.True(vm.CanToggleMicrophone);
    }

    [Fact]
    public async Task NextStart_ClearsThePreviousError()
    {
        _recorder.StartFailure = new InvalidOperationException("no device");
        var vm = Create();
        await vm.StartStopCommand.ExecuteAsync(null);

        _recorder.StartFailure = null;
        await vm.StartStopCommand.ExecuteAsync(null);

        Assert.Null(vm.Error);
        Assert.False(vm.HasError);
        Assert.True(vm.IsRecording);
    }

    [Fact]
    public async Task StopFailure_ShowsInlineError()
    {
        var vm = Create();
        await vm.StartStopCommand.ExecuteAsync(null);
        _recorder.StopFailure = new IOException("disk full");

        await vm.StartStopCommand.ExecuteAsync(null);

        Assert.Equal("Не вдалося зупинити запис: disk full", vm.Error);
    }

    [Fact]
    public async Task StartWithASkippedTrack_ShowsTheWarningInlineAndRecords()
    {
        const string warning = "Мікрофон недоступний: Windows забороняє доступ (Параметри → Конфіденційність і безпека → Мікрофон). Записую лише системний звук.";
        _recorder.StartWarning = warning;
        var vm = Create();

        await vm.StartStopCommand.ExecuteAsync(null);

        Assert.True(vm.IsRecording);
        Assert.Equal(warning, vm.Error);
        Assert.True(vm.HasError);

        await vm.StartStopCommand.ExecuteAsync(null);

        Assert.False(vm.IsRecording);
        Assert.Equal(warning, vm.Error);
    }

    [Fact]
    public async Task StopWithSessionError_ShowsItInline()
    {
        var vm = Create();
        await vm.StartStopCommand.ExecuteAsync(null);
        _recorder.StopError = "Screen segment could not be finalized.";

        await vm.StartStopCommand.ExecuteAsync(null);

        Assert.Equal("Screen segment could not be finalized.", vm.Error);
    }

    [Fact]
    public async Task RecorderStopsBySelf_ReportsTheSessionErrorInline()
    {
        var vm = Create();
        await vm.StartStopCommand.ExecuteAsync(null);
        var id = _recorder.Current!.Id;
        _store.Add(_recorder.Current with { State = SessionState.Failed, Error = "Microphone unplugged." });

        _recorder.FailBySelf();
        await vm.AutoStopReport;

        Assert.False(vm.IsRecording);
        Assert.Equal("Запис зупинився: Microphone unplugged.", vm.Error);
        Assert.True(vm.CanToggleMicrophone);
        Assert.NotEqual(default, id);
    }

    [Fact]
    public async Task UserStop_IsNotReportedAsAnAutomaticStop()
    {
        var vm = Create();
        await vm.StartStopCommand.ExecuteAsync(null);

        await vm.StartStopCommand.ExecuteAsync(null);

        Assert.True(vm.AutoStopReport.IsCompletedSuccessfully);
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task Back_IsOnlyPossibleWhenIdle_AndRaisesBackRequested()
    {
        var vm = Create();
        var raised = 0;
        vm.BackRequested += () => raised++;

        Assert.True(vm.BackCommand.CanExecute(null));
        vm.BackCommand.Execute(null);
        await vm.StartStopCommand.ExecuteAsync(null);
        Assert.False(vm.BackCommand.CanExecute(null));

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Prepare_RefreshesDevicesAndClearsError()
    {
        var vm = Create();
        vm.Error = "old";
        _capture.Microphones.Clear();

        vm.Prepare();

        Assert.Null(vm.Error);
        Assert.Empty(vm.MicrophoneDevices);
        Assert.False(vm.MicrophoneAvailable);
    }

    [Fact]
    public async Task StartStopCommand_IsNotExecutableWhileBusy()
    {
        var vm = Create();

        // Starting/Stopping states come from the recorder; drive them through the fake's event.
        var starting = false;
        _recorder.StateChanged += state => starting |= state == RecorderState.Starting && !vm.StartStopCommand.CanExecute(null);
        await vm.StartStopCommand.ExecuteAsync(null);

        Assert.True(starting);
    }
}
