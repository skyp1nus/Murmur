using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Input;
using Murmur.Core.Recording;
using Murmur.Core.Settings;
using Avalonia.Controls;
using HotkeyAction = Murmur.Core.Input.HotkeyAction;

namespace Murmur.App.Tests;

public sealed class HotkeyCoordinatorTests
{
    private static readonly AppSettings Completed = new() { OnboardingCompleted = true };

    /// <summary>A coordinator over the onboarding rig; the UI thread is a queue the test drains, the background runs inline.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly Queue<Action> _ui = new();
        private readonly OnboardingRig _inner;

        public Rig(AppSettings? settings = null, bool inlineUi = true)
        {
            _inner = new OnboardingRig(settings ?? Completed);
            Hotkeys = new FakeGlobalHotkeys();
            var post = inlineUi ? (Action<Action>)(a => a()) : a => { lock (_ui) { _ui.Enqueue(a); } };
            Coordinator = new HotkeyCoordinator(Hotkeys, Store, Shell, () => ShowAppCalls++, post, a => Background.Add(a));
            RunBackground();
        }

        public FakeGlobalHotkeys Hotkeys { get; }

        public HotkeyCoordinator Coordinator { get; }

        public FakeSettingsStore Store => _inner.Store;

        public ShellViewModel Shell => _inner.Shell;

        public FakeRecorder Recorder => _inner.Recorder;

        public OnboardingRig Onboarding => _inner;

        public int ShowAppCalls { get; private set; }

        public List<Action> Background { get; } = [];

        public HotkeysSettingsViewModel Rows => Shell.Settings!.Hotkeys;

        /// <summary>Runs what the coordinator handed to the background (nothing yet: it has not been started).</summary>
        public void RunBackground()
        {
            while (Background.Count > 0)
            {
                var next = Background[0];
                Background.RemoveAt(0);
                next();
            }
        }

        public void DrainUi()
        {
            while (true)
            {
                Action next;
                lock (_ui)
                {
                    if (_ui.Count == 0)
                    {
                        return;
                    }

                    next = _ui.Dequeue();
                }

                next();
            }
        }

        public void Start()
        {
            Coordinator.Start();
            RunBackground();
        }

        public async Task SaveAsync(Func<AppSettings, AppSettings> edit)
        {
            await Store.SaveAsync(edit(Store.Current), CancellationToken.None);
            RunBackground();
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            _inner.Dispose();
        }
    }

    [Fact]
    public void Constructing_RegistersNothing_Start_RegistersTheThreeGesturesOffTheCallingThread()
    {
        using var rig = new Rig();
        Assert.Empty(rig.Hotkeys.Calls);

        rig.Coordinator.Start();

        // Start only queues the work: nothing is registered on the caller's thread.
        Assert.Empty(rig.Hotkeys.Calls);
        Assert.Single(rig.Background);
        rig.RunBackground();

        Assert.Equal(
            [
                (HotkeyAction.ToggleRecording, "Ctrl+Shift+R"),
                (HotkeyAction.ToggleScreen, "Ctrl+Shift+S"),
                (HotkeyAction.ShowApp, "Ctrl+Shift+M"),
            ],
            rig.Hotkeys.Calls.Select(c => (c.Action, c.Gesture)).ToArray());
    }

    [Fact]
    public void Start_IsIdempotent()
    {
        using var rig = new Rig();
        rig.Start();
        rig.Start();

        Assert.Equal(3, rig.Hotkeys.Calls.Count);
        Assert.Equal(1, rig.Hotkeys.SubscriberCount);
    }

    [Fact]
    public void Start_SkipsActionsWithoutAGesture()
    {
        using var rig = new Rig(Completed with { ToggleScreenHotkey = null });

        rig.Start();

        Assert.Equal([HotkeyAction.ToggleRecording, HotkeyAction.ShowApp], rig.Hotkeys.Calls.Select(c => c.Action).ToArray());
        Assert.Equal(HotkeyRegistration.Cleared, rig.Coordinator.RegistrationOf(HotkeyAction.ToggleScreen));
    }

    [Fact]
    public async Task ChangingOneGesture_RegistersOnlyThatAction()
    {
        using var rig = new Rig();
        rig.Start();
        var before = rig.Hotkeys.Calls.Count;

        await rig.SaveAsync(s => s with { RecordHotkey = "Ctrl+Alt+F9" });

        var after = rig.Hotkeys.Calls.Skip(before).ToArray();
        Assert.NotEmpty(after);
        Assert.All(after, c => Assert.Equal(HotkeyAction.ToggleRecording, c.Action));
        Assert.Equal("Ctrl+Alt+F9", after[^1].Gesture);
    }

    [Fact]
    public async Task ChangingAnotherSetting_RegistersNothing()
    {
        using var rig = new Rig();
        rig.Start();
        var before = rig.Hotkeys.Calls.Count;

        await rig.SaveAsync(s => s with { Theme = ThemePreference.Dark });
        await rig.SaveAsync(s => s with { ScreenByDefault = true });

        Assert.Equal(before, rig.Hotkeys.Calls.Count);
    }

    [Fact]
    public async Task ClearingAGesture_ReleasesIt_WithoutANote()
    {
        using var rig = new Rig();
        rig.Start();

        await rig.SaveAsync(s => s with { ShowAppHotkey = null });

        Assert.Equal((HotkeyAction.ShowApp, (string?)null), (rig.Hotkeys.Calls[^1].Action, rig.Hotkeys.Calls[^1].Gesture));
        Assert.Equal(HotkeyRegistration.Cleared, rig.Coordinator.RegistrationOf(HotkeyAction.ShowApp));
        Assert.False(rig.Rows.Rows[2].HasRegistrationNote);
    }

    [Fact]
    public async Task TwoActionsSwappingGestures_BothGetTheirNewOne()
    {
        using var rig = new Rig();
        rig.Hotkeys.Exclusive = true;
        rig.Start();

        await rig.SaveAsync(s => s with { RecordHotkey = "Ctrl+Shift+S", ToggleScreenHotkey = "Ctrl+Shift+R" });

        Assert.Equal(HotkeyRegistration.Registered, rig.Coordinator.RegistrationOf(HotkeyAction.ToggleRecording));
        Assert.Equal(HotkeyRegistration.Registered, rig.Coordinator.RegistrationOf(HotkeyAction.ToggleScreen));
        Assert.Equal(HotkeyRegistration.Registered, rig.Coordinator.RegistrationOf(HotkeyAction.ShowApp));
    }

    [Fact]
    public void ARegisterThatThrows_IsReportedAsUnsupported_NotAsACrash()
    {
        using var rig = new Rig();
        rig.Hotkeys.Failure = new ObjectDisposedException("hotkeys");

        rig.Start();

        Assert.Equal(HotkeyRegistration.Unsupported, rig.Coordinator.RegistrationOf(HotkeyAction.ToggleRecording));
    }

    [Fact]
    public async Task TakenAndUnsupported_ShowANoteUnderTheirRow_RegisteredAndClearedDoNot()
    {
        using var rig = new Rig(Completed with { ShowAppHotkey = null });
        rig.Hotkeys.Results["Ctrl+Shift+R"] = HotkeyRegistration.Taken;
        rig.Hotkeys.Results["Ctrl+Shift+S"] = HotkeyRegistration.Unsupported;

        rig.Start();

        var rows = rig.Rows.Rows;
        Assert.Equal("Зайнято іншою програмою", rows[0].RegistrationNote);
        Assert.True(rows[0].ShowRegistrationNote);
        Assert.Equal(Strings.Hotkeys_Unsupported, rows[1].RegistrationNote);
        Assert.Null(rows[2].RegistrationNote);
        Assert.False(rows[2].ShowRegistrationNote);

        // The next registration that works hides the note again.
        rig.Hotkeys.Results.Remove("Ctrl+Shift+R");
        await rig.SaveAsync(s => s with { RecordHotkey = "Ctrl+Alt+R" });
        Assert.Null(rows[0].RegistrationNote);
    }

    [Fact]
    public void TheNote_StepsAsideWhileTheRowCapturesOrShowsItsOwnError()
    {
        using var rig = new Rig();
        rig.Hotkeys.Results["Ctrl+Shift+R"] = HotkeyRegistration.Taken;
        rig.Start();
        var row = rig.Rows.Rows[0];
        Assert.True(row.ShowRegistrationNote);

        row.CaptureCommand.Execute(null);
        Assert.False(row.ShowRegistrationNote);

        rig.Rows.CancelCapture();
        Assert.True(row.ShowRegistrationNote);
        row.Error = "x";
        Assert.False(row.ShowRegistrationNote);
    }

    [Fact]
    public void ANewGestureInTheSettings_ClearsTheOldNoteAtOnce()
    {
        using var rig = new Rig();
        rig.Hotkeys.Results["Ctrl+Shift+R"] = HotkeyRegistration.Taken;
        rig.Start();
        var row = rig.Rows.Rows[0];
        Assert.True(row.HasRegistrationNote);

        row.CaptureCommand.Execute(null);
        rig.Rows.HandleKey(Avalonia.Input.Key.F9, Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Alt);

        // Before the coordinator has answered (its work is still queued).
        Assert.False(row.HasRegistrationNote);
    }

    [Fact]
    public void Pressed_OnAnotherThread_OnlyPostsToTheUiThread()
    {
        using var rig = new Rig(inlineUi: false);
        rig.Start();
        rig.DrainUi();

        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);

        // The handler returned without touching the recorder.
        Assert.Empty(rig.Recorder.Starts);
        rig.DrainUi();
        Assert.Single(rig.Recorder.Starts);
        Assert.Same(rig.Shell.Recording, rig.Shell.CurrentPage);
    }

    [Fact]
    public void ToggleRecording_WhenIdle_StartsWithTheSavedDefaults_AndShowsTheRecordingScreen()
    {
        using var rig = new Rig(Completed with { MicrophoneDeviceId = "mic-usb", SystemAudioDeviceId = "out-hdmi", ScreenByDefault = true, ScreenDisplayId = "display-2" });
        rig.Start();
        Assert.Same(rig.Shell.Library, rig.Shell.CurrentPage);

        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);

        var options = Assert.Single(rig.Recorder.Starts);
        Assert.True(options.Microphone);
        Assert.True(options.SystemAudio);
        Assert.True(options.Screen);
        Assert.Equal("mic-usb", options.MicrophoneDeviceId);
        Assert.Equal("out-hdmi", options.SystemDeviceId);
        Assert.Equal("display-2", options.DisplayId);
        Assert.Same(rig.Shell.Recording, rig.Shell.CurrentPage);
        Assert.Equal(RecorderState.Recording, rig.Shell.Recording.State);
    }

    [Fact]
    public void ToggleRecording_WhileRecording_StopsLikeTheStopButton_AndReturnsToTheLibrary()
    {
        using var rig = new Rig();
        rig.Start();
        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);

        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);

        Assert.Equal(1, rig.Recorder.Stops);
        Assert.Equal(RecorderState.Idle, rig.Recorder.State);
        Assert.Same(rig.Shell.Library, rig.Shell.CurrentPage);

        // And again: a new recording.
        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);
        Assert.Equal(2, rig.Recorder.Starts.Count);
    }

    [Fact]
    public void ToggleRecording_WhileTheRecorderIsStopping_IsIgnored()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Recorder.StopGate = gate.Task;
        rig.Start();
        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);
        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);
        Assert.Equal(RecorderState.Stopping, rig.Recorder.State);

        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);
        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleScreen);

        Assert.Equal(1, rig.Recorder.Stops);
        Assert.Single(rig.Recorder.Starts);
        Assert.Empty(rig.Recorder.ScreenCalls);
        gate.SetResult();
    }

    [Fact]
    public void ToggleRecording_WithNoSourceToRecord_OpensTheScreenButStartsNothing()
    {
        using var rig = new Rig();
        rig.Onboarding.Devices.Microphones.Clear();
        rig.Onboarding.Devices.Outputs.Clear();
        rig.Start();

        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);

        Assert.Empty(rig.Recorder.Starts);
        Assert.Same(rig.Shell.Recording, rig.Shell.CurrentPage);
        Assert.True(rig.Shell.Recording.HasSourceHint);
    }

    [Fact]
    public async Task ToggleRecording_IsIgnoredWhileTheOnboardingIsShown_AlsoOnItsTrialRecordingStep()
    {
        using var rig = new Rig(new AppSettings());
        rig.Start();
        Assert.IsType<OnboardingViewModel>(rig.Shell.CurrentPage);

        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);
        Assert.Empty(rig.Recorder.Starts);

        // Step 5 runs its own trial recording; the hotkeys neither stop it nor start another one.
        await rig.Onboarding.GoToStepAsync(5);
        var starts = rig.Recorder.Starts.Count;
        var stops = rig.Recorder.Stops;
        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);
        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);
        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleScreen);

        Assert.Equal(starts, rig.Recorder.Starts.Count);
        Assert.Equal(stops, rig.Recorder.Stops);
        Assert.Empty(rig.Recorder.ScreenCalls);
        Assert.IsType<OnboardingViewModel>(rig.Shell.CurrentPage);
    }

    [Fact]
    public void ToggleScreen_WorksOnlyWhileRecording_AndFlipsTheScreenEachTime()
    {
        using var rig = new Rig();
        rig.Start();

        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleScreen);
        Assert.Empty(rig.Recorder.ScreenCalls);

        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);
        Assert.False(rig.Shell.Recording.Screen);
        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleScreen);
        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleScreen);

        Assert.Equal([true, false], rig.Recorder.ScreenCalls);
        Assert.False(rig.Shell.Recording.Screen);
    }

    [Fact]
    public void ToggleScreen_FollowsThePillWhenTheUserClickedItFirst()
    {
        using var rig = new Rig();
        rig.Start();
        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);
        rig.Shell.Recording.Screen = true;

        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleScreen);

        Assert.Equal([true, false], rig.Recorder.ScreenCalls);
    }

    [Fact]
    public void ToggleScreen_WhenTheRecorderRefuses_PutsThePillBack()
    {
        using var rig = new Rig();
        rig.Start();
        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);
        rig.Recorder.ScreenFailure = new InvalidOperationException("no display");

        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleScreen);

        Assert.False(rig.Shell.Recording.Screen);
        Assert.True(rig.Shell.Recording.HasError);
    }

    [Fact]
    public void ShowApp_RunsTheShowAction()
    {
        using var rig = new Rig();
        rig.Start();

        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ShowApp);

        Assert.Equal(1, rig.ShowAppCalls);
    }

    [Fact]
    public void WhileTheSettingsWaitForAKeyCombination_PressesAreIgnored()
    {
        using var rig = new Rig();
        rig.Start();
        rig.Rows.Rows[0].CaptureCommand.Execute(null);

        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);
        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ShowApp);

        Assert.Empty(rig.Recorder.Starts);
        Assert.Equal(0, rig.ShowAppCalls);

        rig.Rows.CancelCapture();
        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ShowApp);
        Assert.Equal(1, rig.ShowAppCalls);
    }

    [Fact]
    public async Task Dispose_StopsListening_AndLaterChangesAndPressesDoNothing()
    {
        using var rig = new Rig();
        rig.Start();
        var calls = rig.Hotkeys.Calls.Count;
        var subscribers = rig.Store.SubscriberCount;

        rig.Coordinator.Dispose();
        await rig.SaveAsync(s => s with { RecordHotkey = "Ctrl+Alt+F9" });
        rig.Hotkeys.PressFromAnotherThread(HotkeyAction.ToggleRecording);

        Assert.Equal(calls, rig.Hotkeys.Calls.Count);
        Assert.Equal(0, rig.Hotkeys.SubscriberCount);
        Assert.Equal(subscribers - 1, rig.Store.SubscriberCount);
        Assert.Empty(rig.Recorder.Starts);
    }

    [Fact]
    public void WithoutHotkeySupport_EveryGestureGetsTheUnsupportedNote()
    {
        using var rig = new Rig();
        using var coordinator = new HotkeyCoordinator(new UnsupportedGlobalHotkeys(), rig.Store, rig.Shell, () => { }, a => a(), a => a());

        coordinator.Start();

        Assert.All(rig.Rows.Rows, row => Assert.Equal(Strings.Hotkeys_Unsupported, row.RegistrationNote));
    }

    [Fact]
    public async Task MainWindow_BringToFront_ShowsAHiddenWindow_AndRestoresAMinimizedOne()
    {
        var harness = new UiHarness();
        var shell = harness.CreateShell();

        var (hiddenThenShown, minimizedThenRestored, maximizedAgain) = await UiHarness.Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                window.Hide();
                var wasHidden = !window.IsVisible;
                window.BringToFront();
                var shown = wasHidden && window.IsVisible;

                window.WindowState = WindowState.Minimized;
                window.BringToFront();
                var restored = window.WindowState == WindowState.Normal;

                window.WindowState = WindowState.Maximized;
                window.WindowState = WindowState.Minimized;
                window.BringToFront();
                var maximized = window.WindowState == WindowState.Maximized;
                window.Close();
                return (shown, restored, maximized);
            },
            TestContext.Current.CancellationToken);

        Assert.True(hiddenThenShown);
        Assert.True(minimizedThenRestored);
        Assert.True(maximizedAgain);
    }
}
