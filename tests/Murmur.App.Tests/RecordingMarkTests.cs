using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Input;
using Murmur.Core.Recording;

namespace Murmur.App.Tests;

/// <summary>Marking moments on the Recording screen (#45): the Mark button, the feedback chip, the hotkey and the Settings row.</summary>
public sealed class RecordingMarkTests : IDisposable
{
    private readonly IDisposable _english = UiCulture.Override("en");
    private readonly FakeRecorder _recorder = new();
    private readonly FakeBookmarkStore _bookmarks = new();
    private readonly FakeCapture _capture = new();
    private readonly FakeSessionStore _store = new();
    private readonly TestTime _time = new(UiHarness.Now);
    private readonly RecordingMarks _marks;

    public RecordingMarkTests() => _marks = new RecordingMarks(_recorder, _bookmarks);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _marks.Dispose();
        _english.Dispose();
    }

    private RecordingViewModel Create(IRecordingMarks? marks = null) =>
        new(_recorder, _capture, _store, action => action(), time: _time, marks: marks ?? _marks);

    // ---- the Mark button ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMarkButton_IsOnlyAvailableWhileRecordingOrPaused()
    {
        var vm = Create();
        Assert.False(vm.MarkCommand.CanExecute(null));
        Assert.False(vm.IsMarkVisible);

        await vm.StartStopAsync();
        Assert.True(vm.MarkCommand.CanExecute(null));
        Assert.True(vm.IsMarkVisible);

        await vm.PauseResumeCommand.ExecuteAsync(null);
        Assert.True(vm.MarkCommand.CanExecute(null));

        await vm.StartStopAsync();
        Assert.False(vm.MarkCommand.CanExecute(null));
        Assert.False(vm.IsMarkVisible);
    }

    [Fact]
    public async Task TheMarkButton_AddsOneMarkAtTheRecordersElapsedTime()
    {
        var vm = Create();
        await vm.StartStopAsync();
        _recorder.Elapsed = TimeSpan.FromSeconds(761);

        vm.MarkCommand.Execute(null);

        Assert.Equal([TimeSpan.FromSeconds(761)], _marks.Current);
    }

    [Fact]
    public async Task AMarkMadeWhilePaused_GetsThePositionOfThePause_AndMarksAreSavedWhenTheRecordingStops()
    {
        var vm = Create();
        await vm.StartStopAsync();
        var session = _recorder.Current!.Id;
        _recorder.Elapsed = TimeSpan.FromSeconds(30);
        vm.MarkCommand.Execute(null);
        await vm.PauseResumeCommand.ExecuteAsync(null);
        vm.MarkCommand.Execute(null);
        await vm.PauseResumeCommand.ExecuteAsync(null);
        _recorder.Elapsed = TimeSpan.FromSeconds(50);
        vm.MarkCommand.Execute(null);

        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(50)], _marks.Current);

        await vm.StartStopAsync();
        await _marks.PendingFlush;

        var saved = await _bookmarks.GetBookmarksAsync(session, Ct);
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(50)], saved.Select(b => b.At));
        Assert.Empty(_marks.Current);
    }

    [Fact]
    public void WithoutMarks_TheButtonDoesNothing()
    {
        var vm = new RecordingViewModel(_recorder, _capture, _store, action => action(), time: _time);

        Assert.False(vm.MarkCommand.CanExecute(null));
        vm.MarkFromHotkey();
        Assert.False(vm.IsMarkChipShown);
    }

    [Fact]
    public async Task TheDesignDemoSwitch_HidesTheButtonAndTheChipSlot()
    {
        var vm = Create();
        await vm.StartStopAsync();
        Assert.True(vm.ShowMark);

        vm.ShowMark = false;

        Assert.False(vm.IsMarkVisible);
    }

    // ---- the feedback chip -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheChip_SaysMarkedAndTheTime_ForTwoSeconds_ThenHides()
    {
        var vm = Create();
        await vm.StartStopAsync();
        Assert.False(vm.IsMarkChipShown);
        _recorder.Elapsed = new TimeSpan(0, 12, 41);

        vm.MarkCommand.Execute(null);

        Assert.True(vm.IsMarkChipShown);
        Assert.Equal("Marked", vm.MarkChipLabel);
        Assert.Equal("12:41", vm.MarkChipTime);

        _time.Advance(TimeSpan.FromMilliseconds(1900));
        Assert.True(vm.IsMarkChipShown);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(vm.IsMarkChipShown);
    }

    [Fact]
    public async Task TheChip_ShowsForAMarkFromAnySource_AndAFurtherMarkRestartsTheTwoSeconds()
    {
        var vm = Create();
        await vm.StartStopAsync();
        _recorder.Elapsed = TimeSpan.FromSeconds(5);
        _marks.TryMark(out _); // the hotkey or the floating orb call the shared marks, not the button

        Assert.True(vm.IsMarkChipShown);
        Assert.Equal("00:05", vm.MarkChipTime);

        _time.Advance(TimeSpan.FromMilliseconds(1500));
        _recorder.Elapsed = TimeSpan.FromSeconds(7);
        _marks.TryMark(out _);
        Assert.Equal("00:07", vm.MarkChipTime);
        _time.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.True(vm.IsMarkChipShown); // the first mark's two seconds are over, the second's are not
        _time.Advance(TimeSpan.FromMilliseconds(600));
        Assert.False(vm.IsMarkChipShown);
    }

    [Fact]
    public async Task TheChip_ShowsHoursFromOneHourOn()
    {
        var vm = Create();
        await vm.StartStopAsync();
        _recorder.Elapsed = new TimeSpan(1, 2, 3);

        vm.MarkFromHotkey();

        Assert.Equal("1:02:03", vm.MarkChipTime);
    }

    [Fact]
    public async Task TheChip_HidesWhenTheRecordingEnds()
    {
        var vm = Create();
        await vm.StartStopAsync();
        vm.MarkCommand.Execute(null);
        Assert.True(vm.IsMarkChipShown);

        await vm.StartStopAsync();

        Assert.False(vm.IsMarkChipShown);
    }

    // ---- the view ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheScreen_HasTheMarkButtonNextToPause_AsHighAsPause_AndTheChipOnlyAfterAMark()
    {
        using var english = UiCulture.Override("en");
        var harness = new UiHarness();
        var marks = new RecordingMarks(harness.Recorder, _bookmarks);
        var vm = new RecordingViewModel(harness.Recorder, harness.Capture, harness.Store, action => action(), time: harness.Time, marks: marks);
        var shell = new ShellViewModel(harness.CreateShell().Library, vm);
        shell.ShowRecording();

        var (idleVisible, running, chipBefore, chipAfter, label) = await UiHarness.Session.Dispatch(
            async () =>
            {
                var window = UiHarness.Show(shell);
                var view = window.GetVisualDescendants().OfType<RecordingView>().Single();
                Button? Find(string name) => view.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Name == name);
                var idle = Find("RecordingMarkButton") is { IsEffectivelyVisible: true };

                await vm.StartStopAsync();
                UiHarness.Pump();
                var mark = Find("RecordingMarkButton")!;
                var pause = view.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.PauseResumeCommand));
                var chip = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MarkFeedbackChip");
                var before = chip.IsEffectivelyVisible;

                mark.Command!.Execute(null);
                UiHarness.Pump();
                var after = chip.IsEffectivelyVisible;
                var text = string.Join(" ", chip.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
                var running = (mark.IsEffectivelyVisible, mark.Bounds.Height == pause.Bounds.Height, mark.Bounds.Height, mark.Bounds.X > pause.Bounds.X);
                window.Close();
                return (idle, running, before, after, text);
            },
            Ct);

        Assert.False(idleVisible);
        Assert.True(running.Item1);
        Assert.True(running.Item2, "the Mark button is as high as Pause");
        Assert.Equal(32, running.Item3);
        Assert.True(running.Item4, "the Mark button sits right of Pause");
        Assert.False(chipBefore);
        Assert.True(chipAfter);
        Assert.Contains("Marked", label, StringComparison.Ordinal);
        Assert.Contains("00:00", label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InTheSmallestWindow_TheMarkAndStopButtonsAreInsideTheViewport_WithoutScrolling()
    {
        using var english = UiCulture.Override("en");
        var harness = new UiHarness();
        var marks = new RecordingMarks(harness.Recorder, _bookmarks);
        var vm = new RecordingViewModel(harness.Recorder, harness.Capture, harness.Store, action => action(), time: harness.Time, marks: marks);
        var shell = new ShellViewModel(harness.CreateShell().Library, vm);
        shell.ShowRecording();

        var problems = await UiHarness.Session.Dispatch(
            async () =>
            {
                var window = new MainWindow { DataContext = shell, Width = 900, Height = 600 };
                window.Show();
                UiHarness.Pump();
                await vm.StartStopAsync();
                vm.MarkCommand.Execute(null); // the chip is shown: the tallest state
                UiHarness.Pump();
                var found = new List<string>();
                var view = window.GetVisualDescendants().OfType<RecordingView>().Single();
                var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.IsEffectivelyVisible);
                foreach (var name in new[] { "RecordingMarkButton", "StartStopButton" })
                {
                    var button = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
                    var top = button.TranslatePoint(default, scroller) ?? default;
                    var rect = new Rect(top, button.Bounds.Size);
                    if (!button.IsEffectivelyVisible || rect.Top < -0.5 || rect.Bottom > scroller.Viewport.Height + 0.5 || rect.Right > scroller.Viewport.Width + 0.5)
                    {
                        found.Add($"{name} at {rect} is outside the {scroller.Viewport} viewport (offset {scroller.Offset})");
                    }
                }

                if (scroller.Offset.Y != 0)
                {
                    found.Add("the page scrolled by itself");
                }

                window.Close();
                return found;
            },
            Ct);

        Assert.Empty(problems);
    }

    [Fact]
    public async Task AFailedSaveOfTheMarks_ShowsADismissibleNoticeInTheLibrary_ThatAReloadKeeps()
    {
        var harness = new UiHarness();
        var marks = new RecordingMarks(harness.Recorder, _bookmarks);
        var vm = new RecordingViewModel(harness.Recorder, harness.Capture, harness.Store, action => action(), time: harness.Time, marks: marks);
        var shell = new ShellViewModel(harness.CreateShell().Library, vm, post: action => action());
        await shell.Library.LoadAsync();
        _bookmarks.Failure = new IOException("database is locked");
        await vm.StartStopAsync();
        vm.MarkCommand.Execute(null);

        await vm.StartStopAsync();
        await marks.PendingFlush;

        Assert.Equal(Strings.Library_MarksNotSaved, shell.Library.Notice);
        var entry = shell.Library.Entries.OfType<LibraryNoticeEntry>().Single(n => n.Text == Strings.Library_MarksNotSaved);
        Assert.NotNull(entry.Dismiss);
        await shell.Library.LoadAsync();
        Assert.Equal(Strings.Library_MarksNotSaved, shell.Library.Notice);

        entry.Dismiss!.Execute(null);

        Assert.Null(shell.Library.Notice);
        Assert.DoesNotContain(shell.Library.Entries.OfType<LibraryNoticeEntry>(), n => n.Text == Strings.Library_MarksNotSaved);
    }

    [Fact]
    public async Task ASuccessfulSave_ShowsNoNotice()
    {
        var harness = new UiHarness();
        var marks = new RecordingMarks(harness.Recorder, _bookmarks);
        var vm = new RecordingViewModel(harness.Recorder, harness.Capture, harness.Store, action => action(), time: harness.Time, marks: marks);
        var shell = new ShellViewModel(harness.CreateShell().Library, vm, post: action => action());
        await vm.StartStopAsync();
        vm.MarkCommand.Execute(null);

        await vm.StartStopAsync();
        await marks.PendingFlush;

        Assert.Null(shell.Library.Notice);
    }

    [Fact]
    public void TheNoticeTextsExistInBothLanguages()
    {
        foreach (var key in new[] { nameof(Strings.Library_MarksNotSaved), nameof(Strings.Library_DismissNotice) })
        {
            using (UiCulture.Override("uk"))
            {
                var uk = Strings.Get(key);
                using (UiCulture.Override("en"))
                {
                    Assert.NotEqual(Strings.Get(key), uk);
                }
            }
        }
    }

    // ---- the hotkey ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheShellsMarkHotkey_MarksTheRunningRecording_AndDoesNothingWhenIdle()
    {
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        shell.ShowRecording();
        var marks = new RecordingMarks(harness.Recorder, _bookmarks);
        var page = new ShellViewModel(shell.Library, new RecordingViewModel(harness.Recorder, harness.Capture, harness.Store, action => action(), time: harness.Time, marks: marks));

        page.MarkFromHotkey();
        Assert.Empty(marks.Current);

        await page.Recording.StartStopAsync();
        harness.Recorder.Elapsed = TimeSpan.FromSeconds(9);
        page.MarkFromHotkey();

        Assert.Equal([TimeSpan.FromSeconds(9)], marks.Current);
        marks.Dispose();
    }

    // ---- Settings -> Hotkeys ------------------------------------------------------------------------------------------------

    private sealed class SettingsRig : IDisposable
    {
        public SettingsRig() => Rig = new OnboardingRig(new Murmur.Core.Settings.AppSettings { OnboardingCompleted = true });

        public OnboardingRig Rig { get; }

        public HotkeysSettingsViewModel Hotkeys => Rig.Settings.Hotkeys;

        public HotkeyRowViewModel Row => Hotkeys.Rows.Single(r => r.Action == HotkeyAction.Mark);

        public void Dispose() => Rig.Dispose();
    }

    [Fact]
    public void TheMarkMomentRow_ShowsTheDefaultGesture()
    {
        using var rig = new SettingsRig();

        Assert.Equal("Mark moment", rig.Row.Label);
        Assert.Equal("Ctrl+Alt+M", rig.Row.Gesture);
        Assert.Equal("Ctrl Alt M", rig.Row.DisplayText);
        Assert.True(rig.Row.IsShown);
    }

    [Fact]
    public async Task TheMarkMomentRow_CapturesAndSavesAGesture_AndClearsIt()
    {
        using var rig = new SettingsRig();

        rig.Row.CaptureCommand.Execute(null);
        Assert.True(rig.Row.IsCapturing);
        Assert.True(rig.Hotkeys.HandleKey(Key.F8, KeyModifiers.Control | KeyModifiers.Alt));
        await rig.Hotkeys.LastSave;

        Assert.Equal("Ctrl+Alt+F8", rig.Rig.Store.Current.MarkHotkey);
        Assert.Equal("Ctrl+Alt+F8", rig.Row.Gesture);

        rig.Row.CaptureCommand.Execute(null);
        Assert.True(rig.Hotkeys.HandleKey(Key.Back, KeyModifiers.None));
        await rig.Hotkeys.LastSave;

        Assert.Null(rig.Rig.Store.Current.MarkHotkey);
        Assert.Equal(Strings.Hotkeys_None, rig.Row.DisplayText);
    }

    [Fact]
    public async Task TheMarkMomentRow_RefusesAGestureAnotherActionHas_AndTheOtherWayRound()
    {
        using var rig = new SettingsRig();

        rig.Row.CaptureCommand.Execute(null);
        rig.Hotkeys.HandleKey(Key.M, KeyModifiers.Control | KeyModifiers.Shift); // "Open Murmur" has it
        Assert.True(rig.Row.IsCapturing);
        Assert.Equal(UiText.Format(Strings.Hotkeys_Duplicate, Strings.Hotkeys_ShowApp), rig.Row.Error);
        Assert.Equal("Ctrl+Alt+M", rig.Rig.Store.Current.MarkHotkey);
        rig.Hotkeys.CancelCapture();

        var showApp = rig.Hotkeys.Rows.Single(r => r.Action == HotkeyAction.ShowApp);
        showApp.CaptureCommand.Execute(null);
        rig.Hotkeys.HandleKey(Key.M, KeyModifiers.Control | KeyModifiers.Alt); // "Mark moment" has it
        Assert.Equal(UiText.Format(Strings.Hotkeys_Duplicate, Strings.Hotkeys_Mark), showApp.Error);
        await Task.CompletedTask;
    }

    [Fact]
    public void TheDesignDemoSwitch_HidesTheMarkMomentRow()
    {
        using var rig = new SettingsRig();

        rig.Hotkeys.ShowMark = false;

        Assert.False(rig.Row.IsShown);
        Assert.All(rig.Hotkeys.Rows.Where(r => r.Action != HotkeyAction.Mark), r => Assert.True(r.IsShown));
    }

    [Fact]
    public void TheMarkTextsExistInBothLanguages()
    {
        foreach (var key in new[] { nameof(Strings.Recording_Mark), nameof(Strings.Recording_Marked), nameof(Strings.Hotkeys_Mark) })
        {
            using (UiCulture.Override("en"))
            {
                Assert.False(string.IsNullOrWhiteSpace(Strings.Get(key)));
            }

            using (UiCulture.Override("uk"))
            {
                var uk = Strings.Get(key);
                Assert.False(string.IsNullOrWhiteSpace(uk));
                using (UiCulture.Override("en"))
                {
                    Assert.NotEqual(Strings.Get(key), uk);
                }
            }
        }
    }
}
