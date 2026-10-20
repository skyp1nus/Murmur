using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

/// <summary>The Recording screen on the headless platform, and the controls it is made of.</summary>
public sealed class RecordingViewTests
{
    private static Avalonia.Headless.HeadlessUnitTestSession Session => UiHarness.Session;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IEnumerable<string?> Texts(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text);

    private static T Single<T>(Visual root, Func<T, bool>? where = null)
        where T : Visual => root.GetVisualDescendants().OfType<T>().Single(v => where is null || where(v));

    // ---- the screen --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhileRecording_TheScreenHasThePauseAndStopButtons_TheTracksCardAndTheMessageCard()
    {
        using var english = UiCulture.Override("en");
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        shell.ShowRecording();
        harness.Recorder.Levels = new AudioLevels(0.126f, 0.063f);

        var (idle, running, paused) = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override("en");
                var window = UiHarness.Show(shell);
                var view = Single<RecordingView>(window);
                var idleTexts = Texts(view).ToArray();

                await shell.Recording.StartStopAsync();
                shell.Recording.Refresh();
                UiHarness.Pump();
                var runningTexts = Texts(view).ToArray();

                await shell.Recording.PauseResumeCommand.ExecuteAsync(null);
                UiHarness.Pump();
                var pausedTexts = Texts(view).ToArray();
                window.Close();
                return (idleTexts, runningTexts, pausedTexts);
            },
            Ct);

        Assert.Contains("Start recording", idle);
        Assert.DoesNotContain("Pause", idle);
        Assert.DoesNotContain("Tracks", idle);
        Assert.Contains("Let people know", idle);
        Assert.Contains("Back", idle);

        Assert.Contains("Pause", running);
        Assert.Contains("Stop and transcribe", running);
        Assert.Contains("Tracks", running);
        Assert.Contains("You · microphone", running);
        Assert.Contains("Others · system audio", running);
        Assert.Contains("−18 dB", running);
        Assert.Contains("−24 dB", running);
        Assert.Contains("Built-in microphone", running);
        Assert.Contains("Speakers · loopback", running);
        Assert.Contains("Auto-saved to disk every few seconds", running);
        Assert.Contains("Copy message", running);
        Assert.DoesNotContain("Back", running);
        Assert.DoesNotContain("Start recording", running);

        Assert.Contains("Resume", paused);
        Assert.DoesNotContain("Pause", paused);
        Assert.Contains("Paused", paused); // the track states
        Assert.Contains("Paused — nothing is recorded until you resume", paused);
    }

    [Fact]
    public async Task ThePauseButton_PausesAndResumesTheRecorder()
    {
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        shell.ShowRecording();

        var states = await Session.Dispatch(
            async () =>
            {
                var window = UiHarness.Show(shell);
                await shell.Recording.StartStopAsync();
                UiHarness.Pump();
                var pause = Single<Button>(window, b => b.Command == shell.Recording.PauseResumeCommand);
                pause.Command!.Execute(null);
                await Task.Yield();
                UiHarness.Pump();
                var afterPause = (harness.Recorder.State, shell.Recording.PauseLabel);
                pause.Command!.Execute(null);
                await Task.Yield();
                UiHarness.Pump();
                var afterResume = (harness.Recorder.State, shell.Recording.PauseLabel);
                window.Close();
                return (afterPause, afterResume);
            },
            Ct);

        Assert.Equal(RecorderState.Paused, states.afterPause.State);
        Assert.Equal(RecorderState.Recording, states.afterResume.State);
    }

    [Fact]
    public async Task TheTitleField_Enter_SavesAndEsc_Cancels()
    {
        using var english = UiCulture.Override("en");
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        shell.ShowRecording();
        var store = new FakeSessionStore();
        harness.Recorder.AfterStart = () => store.Add(harness.Recorder.Current!);
        var recording = new RecordingViewModel(harness.Recorder, harness.Capture, store, action => action(), time: harness.Time);
        var page = new ShellViewModel(shell.Library, recording);
        page.ShowRecording();

        var (afterEnter, afterEsc, stored) = await Session.Dispatch(
            async () =>
            {
                var window = UiHarness.Show(page);
                await recording.StartStopAsync();
                UiHarness.Pump();
                var box = Single<TextBox>(window, t => t.Name == "TitleBox");

                box.Focus();
                box.Text = "Budget review";
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                UiHarness.Pump();
                await Task.Delay(50, Ct);
                var enter = (recording.Title, box.IsFocused);

                box.Focus();
                box.Text = "Something else";
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                UiHarness.Pump();
                await Task.Delay(50, Ct);
                var esc = (box.Text, recording.Title);
                var title = store.Get(harness.Recorder.Current!.Id)!.Title;
                window.Close();
                return (enter, esc, title);
            },
            Ct);

        Assert.Equal("Budget review", afterEnter.Title);
        Assert.False(afterEnter.IsFocused);
        Assert.Equal("Budget review", afterEsc.Text);
        Assert.Equal("Budget review", afterEsc.Title);
        Assert.Equal("Budget review", stored);
    }

    [Fact]
    public async Task CopyMessage_ButtonWritesToTheWindowClipboard_AndSaysCopied()
    {
        using var english = UiCulture.Override("en");
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        shell.ShowRecording();

        var (label, clipboard) = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override("en");
                var window = UiHarness.Show(shell);
                var button = Single<Button>(window, b => b.Command == shell.Recording.CopyMessageCommand);
                button.Command!.Execute(null);
                await Task.Delay(100, Ct);
                UiHarness.Pump();
                var text = window.Clipboard is { } c ? await c.TryGetTextAsync() : null;
                var result = (Single<Button>(window, b => b.Command == shell.Recording.CopyMessageCommand).Content as string, text);
                window.Close();
                return result;
            },
            Ct);

        Assert.Equal("Copied", label);
        Assert.Equal(Strings.Recording_MessageToCopy, clipboard);
    }

    [Fact]
    public async Task TheSourceChips_ReflectTheState_AndOnlyTheScreenOneWorksWhileRecording()
    {
        using var english = UiCulture.Override("en");
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        shell.ShowRecording();

        var (idle, running) = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override("en");
                var window = UiHarness.Show(shell);
                var chips = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Where(b => b.Classes.Contains("source")).ToArray();
                var before = chips.Select(c => (c.IsChecked, c.IsEnabled, (c.Foreground as ISolidColorBrush)?.Color)).ToArray();
                await shell.Recording.StartStopAsync();
                UiHarness.Pump();
                var during = chips.Select(c => (c.IsChecked, c.IsEnabled, (c.Foreground as ISolidColorBrush)?.Color)).ToArray();
                window.Close();
                return (before, during);
            },
            Ct);

        Assert.Equal([(true, true), (true, true), (false, true)], idle.Select(c => (c.IsChecked, c.IsEnabled)));
        Assert.Equal([(true, false), (true, false), (false, true)], running.Select(c => (c.IsChecked, c.IsEnabled)));

        // Disabled by the state, a chip still looks like its state: the same colours as before the start.
        Assert.Equal(idle.Select(c => c.Item3), running.Select(c => c.Item3));
        Assert.NotEqual(idle[0].Item3, idle[2].Item3); // on = Ink, off = Smoke
    }

    // ---- the controls ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AsidePairPanel_PutsTheSideAtTheRightWhenWide_AndBelowWhenNarrow()
    {
        var (wide, narrow) = await Session.Dispatch(
            () =>
            {
                var main = new Border { Height = 300 };
                var side = new Border { Height = 400 };
                var panel = new AsidePairPanel { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, SideWidth = 320, WrapBelow = 872, ColumnSpacing = 32, RowSpacing = 24, Children = { main, side } };
                var window = new Window { Content = panel, Width = 1116, Height = 800, Padding = default };
                window.Show();
                UiHarness.Pump();
                var wideLayout = (main.Bounds, side.Bounds, panel.Bounds.Height);
                window.Width = 700;
                UiHarness.Pump();
                var narrowLayout = (main.Bounds, side.Bounds, panel.Bounds.Height);
                window.Close();
                return (wideLayout, narrowLayout);
            },
            Ct);

        Assert.Equal(new Rect(0, 0, 1116 - 352, 300), wide.Item1);
        Assert.Equal(new Rect(1116 - 320, 0, 320, 400), wide.Item2);
        Assert.Equal(400, wide.Item3);

        Assert.Equal(new Rect(0, 0, 700, 300), narrow.Item1);
        Assert.Equal(new Rect(0, 324, 700, 400), narrow.Item2);
        Assert.Equal(724, narrow.Item3);
    }

    [Fact]
    public async Task LevelHistoryControl_DrawsPastBarsInInkAndFutureOnesLight()
    {
        var pixels = await Session.Dispatch(
            () =>
            {
                var history = new LevelHistory();
                for (var i = 0; i < LevelHistory.PastCount; i++)
                {
                    history.Push(1f);
                }

                var strip = new LevelHistoryControl
                {
                    Width = 360,
                    Height = 36,
                    History = history,
                    PastBrush = Brushes.Black,
                    FutureBrush = Brushes.LightGray,
                };
                var window = new Window { Content = strip, Width = 360, Height = 36, Background = Brushes.White, Padding = default };
                window.Show();
                UiHarness.Pump();
                var frame = UiHarness.Pixels(window);
                window.Close();
                return frame;
            },
            Ct);

        // 36 bars, 3 px apart, 360 px wide: each bar is (360 - 105) / 36 = 7.08 px; the 26 past bars are full height (32 px), the
        // 10 future ones are 4 px stubs.
        const int width = 360;
        byte Red(int x, int y) => pixels[((y * width) + x) * 4];
        static int Centre(int bar) => (int)((bar * (((360.0 - 105) / 36) + 3)) + (((360.0 - 105) / 36) / 2));
        Assert.True(Red(Centre(5), 3) < 40, "a past bar reaches the top");
        Assert.True(Red(Centre(25), 33) < 40, "the newest past bar reaches the bottom");
        Assert.True(Red(Centre(26), 3) > 240, "a future bar is only a stub");
        Assert.InRange(Red(Centre(30), 18), 150, 230); // the stub in the middle is light grey, not ink
    }

    [Theory]
    [InlineData(0f, 4)]
    [InlineData(1f, 32)]
    [InlineData(0.5f, 18)]
    [InlineData(-1f, 4)]
    [InlineData(5f, 32)]
    public void LevelHistoryControl_BarHeightsRunFromFourToThirtyTwoWholePixels(float value, double expected) =>
        Assert.Equal(expected, LevelHistoryControl.BarHeight(value));

    [Fact]
    public void LevelHistory_ShiftsTheStripLeft_AndClears()
    {
        var history = new LevelHistory();
        var changes = 0;
        history.Changed += () => changes++;

        history.Push(1f);
        history.Push(0.1f);

        Assert.Equal(2, changes);
        Assert.Equal(1.0, history.Values[^2], 0.001);
        Assert.Equal(0.667, history.Values[^1], 0.001);
        Assert.Equal(0f, history.Values[0]);

        history.Clear();
        Assert.Equal(3, changes);
        Assert.All(history.Values.ToArray(), v => Assert.Equal(0f, v));
    }
}
