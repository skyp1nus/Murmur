using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Localization;
using Murmur.App.Tests.Design;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Activity;

namespace Murmur.App.Tests;

/// <summary>The status bar and the Activity popover on a real (headless) window.</summary>
public sealed class StatusBarViewTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ScreenshotDir =>
        Environment.GetEnvironmentVariable("MURMUR_SIZEFIT_DIR") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "murmur-sizefit");

    private static void Click(Control control)
    {
        var top = TopLevel.GetTopLevel(control)!;
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        top.MouseDown(point, MouseButton.Left);
        top.MouseUp(point, MouseButton.Left);
        UiHarness.Pump(4);
    }

    [Fact]
    public async Task TheBar_IsAtTheBottomOfTheWindow_AndSaysUpToDate_WhenNothingRuns()
    {
        var result = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                using var rig = new DesignRig(statusBar: true);
                rig.Activity!.Set();
                var window = UiHarness.Show(rig.Shell);
                var bar = window.GetVisualDescendants().OfType<StatusBarView>().Single();
                var texts = bar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray();
                var bottom = bar.TranslatePoint(new Point(0, bar.Bounds.Height), window)!.Value.Y;
                var recording = bar.FindControl<Button>("RecordingButton")!.IsEffectivelyVisible;
                var r = (barHeight: bar.Bounds.Height, bottom, windowHeight: window.Bounds.Height, texts, recording, bar.IsPolling);
                window.Close();
                return r;
            },
            Ct);

        Assert.Equal(28, result.barHeight);
        Assert.Equal(result.windowHeight, result.bottom, 0.5);
        Assert.Equal(["All local", "Up to date"], result.texts);
        Assert.False(result.recording);
        Assert.False(result.IsPolling);
    }

    [Fact]
    public async Task WhileRecording_TheLeftPartShows_AndAClickOpensTheRecordingScreen()
    {
        var (visible, text, onRecording, polling) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                using var rig = new DesignRig(statusBar: true);
                rig.Recorder.Elapsed = TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(48);
                var window = UiHarness.Show(rig.Shell);
                var bar = window.GetVisualDescendants().OfType<StatusBarView>().Single();
                var button = bar.FindControl<Button>("RecordingButton")!;
                var shownBefore = button.IsEffectivelyVisible;
                Assert.Same(rig.Library, rig.Shell.CurrentPage);

                // DesignRig's feed starts with a recording item.
                var label = bar.FindControl<TextBlock>("ElapsedLabel")!.Text;
                Click(button);
                var r = (shownBefore && button.Bounds.Width > 0, label, ReferenceEquals(rig.Shell.CurrentPage, rig.Recording), bar.IsPolling);
                window.Close();
                return r;
            },
            Ct);

        Assert.True(visible);
        Assert.Equal("12:48", text);
        Assert.True(onRecording);
        Assert.True(polling);
    }

    [Fact]
    public async Task TheElapsedTime_IsPolledOncePerSecond_AndStopsWithTheRecording()
    {
        var (polling, stopped) = await Session.Dispatch(
            () =>
            {
                using var rig = new DesignRig(statusBar: true);
                var window = UiHarness.Show(rig.Shell);
                var bar = window.GetVisualDescendants().OfType<StatusBarView>().Single();
                var running = bar.IsPolling;
                rig.Activity!.Set();
                UiHarness.Pump(2);
                var r = (running, !bar.IsPolling);
                window.Close();
                return r;
            },
            Ct);

        Assert.True(polling);
        Assert.True(stopped);
    }

    [Fact]
    public async Task ClickingTheSummary_OpensThePopover_WithTheRowsOfTheFeed()
    {
        var (shown, texts, bars, dataContextIsActivity) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                using var rig = new DesignRig(statusBar: true);
                var window = UiHarness.Show(rig.Shell);
                var bar = window.GetVisualDescendants().OfType<StatusBarView>().Single();
                var button = bar.FindControl<Button>("ActivityButton")!;
                var flyout = (Flyout)button.Flyout!;

                Click(button);
                var content = (ActivityPopoverView)flyout.Content!;
                var visual = content.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToArray();
                var strings = visual.Select(t => t.Inlines is { Count: > 0 } ? string.Concat(t.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : t.Text ?? string.Empty).ToArray();
                UiHarness.Save(window, Path.Combine(ScreenshotDir, "ActivityPopover-light.png"));
                var r = (flyout.IsOpen, strings, content.GetVisualDescendants().OfType<ProgressBar>().Count(p => p.IsEffectivelyVisible), content.DataContext == rig.Shell.StatusBar!.Activity);
                flyout.Hide();
                window.Close();
                return r;
            },
            Ct);

        Assert.True(shown);
        Assert.True(dataContextIsActivity);
        Assert.Equal(2, bars);
        Assert.Equal(
            [
                "Activity", "All on this PC",
                "Recording · Untitled recording", "00:00",
                "Transcribing · Weekly team sync", "64%",
                "Downloading · Gemma 4 26B", "42%",
                "Indexing · Weekly team sync", "Queued",
            ],
            texts);
    }

    [Fact]
    public async Task ThePopover_ShowsTheEmptyState_AndFollowsTheFeedWhileOpen()
    {
        var (empty, afterwards) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                using var rig = new DesignRig(statusBar: true);
                rig.Activity!.Set();
                var window = UiHarness.Show(rig.Shell);
                var button = window.GetVisualDescendants().OfType<StatusBarView>().Single().FindControl<Button>("ActivityButton")!;
                var flyout = (Flyout)button.Flyout!;
                Click(button);
                var content = (ActivityPopoverView)flyout.Content!;
                string[] Texts() => [.. content.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty)];
                var first = Texts();
                UiHarness.Save(window, Path.Combine(ScreenshotDir, "ActivityPopover-empty.png"));

                rig.Activity.Set(new ActivityItem("model:x", ActivityKind.DownloadingModel, "Parakeet", 0.5, false));
                UiHarness.Pump(4);
                var second = Texts();
                flyout.Hide();
                window.Close();
                return (first, second);
            },
            Ct);

        Assert.Equal(["Activity", "All on this PC", "Nothing running."], empty);
        Assert.Contains("50%", afterwards);
        Assert.DoesNotContain("Nothing running.", afterwards);
    }

    [Fact]
    public async Task ThePopover_RendersInTheDarkTheme()
    {
        var shown = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                using var rig = new DesignRig(statusBar: true);
                var window = new MainWindow { DataContext = rig.Shell, Width = 1100, Height = 720, RequestedThemeVariant = ThemeVariant.Dark };
                window.Show();
                UiHarness.Pump(6);
                var button = window.GetVisualDescendants().OfType<StatusBarView>().Single().FindControl<Button>("ActivityButton")!;
                Click(button);
                UiHarness.Save(window, Path.Combine(ScreenshotDir, "ActivityPopover-dark.png"));
                var r = ((Flyout)button.Flyout!).IsOpen;
                ((Flyout)button.Flyout!).Hide();
                window.Close();
                return r;
            },
            Ct);

        Assert.True(shown);
    }

    [Fact]
    public async Task WithoutAFeed_TheWindowHasNoStatusBar()
    {
        var count = await Session.Dispatch(
            () =>
            {
                using var rig = new DesignRig();
                var window = UiHarness.Show(rig.Shell);
                var r = window.GetVisualDescendants().OfType<StatusBarView>().Count();
                window.Close();
                return r;
            },
            Ct);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ClosingTheWindow_ReleasesTheFeed()
    {
        var subscribed = await Session.Dispatch(
            () =>
            {
                using var rig = new DesignRig(statusBar: true);
                var window = UiHarness.Show(rig.Shell);
                Assert.True(rig.Activity!.HasSubscribers);
                window.Close();
                UiHarness.Pump(2);
                return rig.Activity.HasSubscribers;
            },
            Ct);

        Assert.False(subscribed);
    }

    [Fact]
    public async Task RecordingClick_DuringTheOnboarding_DoesNotLeaveIt()
    {
        var page = await Session.Dispatch(
            () =>
            {
                using var rig = new DesignRig(onboarding: true, statusBar: true);
                var window = UiHarness.Show(rig.Shell);
                var onboarding = rig.Shell.CurrentPage;
                rig.Shell.StatusBar!.ShowRecordingCommand.Execute(null);
                var r = ReferenceEquals(onboarding, rig.Shell.CurrentPage) && onboarding is OnboardingViewModel;
                window.Close();
                return r;
            },
            Ct);

        Assert.True(page);
    }

    // ---- the spinning orb ---------------------------------------------------------------------------------------------

    private static Window HostOrb(SpinningOrb orb, bool reduce)
    {
        var window = new Window { Width = 200, Height = 200, Content = orb };
        Motion.SetReduceMotion(window, reduce);
        window.Show();
        UiHarness.Pump();
        return window;
    }

    [Fact]
    public async Task SpinningOrb_TurnsOnce_InNineSeconds()
    {
        var angles = await Session.Dispatch(
            () =>
            {
                var orb = new SpinningOrb { Width = 12, Height = 12 };
                var window = HostOrb(orb, reduce: false);
                var clock = new TestTime(UiHarness.Now);
                orb.Clock = clock;
                orb.Step();
                clock.Advance(TimeSpan.FromSeconds(1));
                orb.Step();
                var one = orb.Angle;
                clock.Advance(TimeSpan.FromSeconds(8));
                orb.Step();
                var r = (spinning: orb.IsSpinning, one, full: orb.Angle);
                window.Close();
                return r;
            },
            Ct);

        Assert.True(angles.spinning);
        Assert.Equal(40, angles.one, 1);
        Assert.InRange(angles.full, 0, 0.001);
    }

    [Fact]
    public async Task SpinningOrb_UnderReducedMotion_RunsNoTimer_AndStaysAtRest()
    {
        var (running, angle, restarted) = await Session.Dispatch(
            () =>
            {
                var orb = new SpinningOrb { Width = 12, Height = 12 };
                var window = HostOrb(orb, reduce: true);
                var clock = new TestTime(UiHarness.Now);
                orb.Clock = clock;
                UiHarness.Pump(6);
                var r = (orb.IsSpinning, orb.Angle, false);
                Motion.SetReduceMotion(window, false);
                r.Item3 = orb.IsSpinning;
                window.Close();
                return r;
            },
            Ct);

        Assert.False(running);
        Assert.Equal(0, angle);
        Assert.True(restarted);
    }

    [Fact]
    public async Task SpinningOrb_TurningMotionOff_StopsItAndReturnsToRest()
    {
        var (angle, running) = await Session.Dispatch(
            () =>
            {
                var orb = new SpinningOrb { Width = 12, Height = 12 };
                var window = HostOrb(orb, reduce: false);
                var clock = new TestTime(UiHarness.Now);
                orb.Clock = clock;
                orb.Step();
                clock.Advance(TimeSpan.FromSeconds(2));
                orb.Step();
                Assert.True(orb.Angle > 0);
                Motion.SetReduceMotion(window, true);
                var r = (orb.Angle, orb.IsSpinning);
                window.Close();
                return r;
            },
            Ct);

        Assert.Equal(0, angle);
        Assert.False(running);
    }
}
