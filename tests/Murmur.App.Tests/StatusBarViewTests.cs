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
[Trait("Category", "Slow")]
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
    public async Task TheElapsedTime_IsPolled_WhileRecording_AndStopsWithTheRecording()
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
        UiHarness.Pump(6);
        return window;
    }

    [Fact]
    public async Task SpinningOrb_TurnsOnlyWhileActive()
    {
        var (idle, active, stopped, angle) = await Session.Dispatch(
            () =>
            {
                var orb = new SpinningOrb { Width = 12, Height = 12 };
                var window = HostOrb(orb, reduce: false);
                var before = orb.IsSpinning;
                orb.IsActive = true;
                var turning = orb.IsSpinning;
                orb.IsActive = false;
                var r = (before, turning, !orb.IsSpinning, orb.Angle);
                window.Close();
                return r;
            },
            Ct);

        Assert.False(idle);
        Assert.True(active);
        Assert.True(stopped);
        Assert.Equal(0f, angle);
    }

    [Fact]
    public async Task SpinningOrb_TurnsOnceInNineSeconds_AndGoesBackToRestWhenStopped()
    {
        var (quarter, half, wrapped, rest) = await Session.Dispatch(
            () =>
            {
                var orb = new SpinningOrb { Width = 12, Height = 12, IsActive = true };
                var window = HostOrb(orb, reduce: false);
                var clock = new TestTime(UiHarness.Now);
                orb.Clock = clock;
                clock.Advance(TimeSpan.FromSeconds(2.25));
                orb.Tick();
                var a = orb.Angle;
                clock.Advance(TimeSpan.FromSeconds(2.25));
                orb.Tick();
                var b = orb.Angle;
                clock.Advance(TimeSpan.FromSeconds(4.5));
                orb.Tick();
                var c = orb.Angle;
                orb.IsActive = false;
                var r = (a, b, c, orb.Angle);
                window.Close();
                return r;
            },
            Ct);

        Assert.Equal(MathF.PI / 2, quarter!.Value, 0.01f);
        Assert.Equal(MathF.PI, half!.Value, 0.01f);
        Assert.InRange(wrapped!.Value, 0f, 0.01f);
        Assert.Equal(0f, rest);
    }

    [Fact]
    public async Task ReopeningThePopover_TheRecordingOrbTurnsAgain()
    {
        var (first, closed, second) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                using var rig = new DesignRig(statusBar: true);
                var window = UiHarness.Show(rig.Shell);
                var button = window.GetVisualDescendants().OfType<StatusBarView>().Single().FindControl<Button>("ActivityButton")!;
                var flyout = (Flyout)button.Flyout!;
                bool RowOrbSpins() => ((ActivityPopoverView)flyout.Content!).GetVisualDescendants().OfType<SpinningOrb>().Single(o => o.IsEffectivelyVisible).IsSpinning;

                Click(button);
                var a = RowOrbSpins();
                flyout.Hide();
                UiHarness.Pump(4);
                var b = flyout.IsOpen;
                Click(button);
                var c = flyout.IsOpen && RowOrbSpins();
                flyout.Hide();
                window.Close();
                return (a, b, c);
            },
            Ct);

        Assert.True(first);
        Assert.False(closed);
        Assert.True(second);
    }

    [Fact]
    public async Task SpinningOrb_UnderReducedMotion_HasNoAnimation_UntilMotionIsAllowed()
    {
        var (reduced, allowed) = await Session.Dispatch(
            () =>
            {
                var orb = new SpinningOrb { Width = 12, Height = 12, IsActive = true };
                var window = HostOrb(orb, reduce: true);
                var whileReduced = orb.IsSpinning;
                Motion.SetReduceMotion(window, false);
                var r = (whileReduced, orb.IsSpinning);
                window.Close();
                return r;
            },
            Ct);

        Assert.False(reduced);
        Assert.True(allowed);
    }

    [Fact]
    public async Task SpinningOrb_MotionToggledWhileHidden_FollowsTheSetting()
    {
        var (hiddenReduced, shownAgain) = await Session.Dispatch(
            () =>
            {
                var orb = new SpinningOrb { Width = 12, Height = 12, IsActive = true };
                var window = HostOrb(orb, reduce: false);
                Assert.True(orb.IsSpinning);
                orb.IsVisible = false;
                Motion.SetReduceMotion(window, true);
                var stillWhileHidden = !orb.IsSpinning;
                orb.IsVisible = true;
                UiHarness.Pump(4);
                var r = (stillWhileHidden, orb.IsSpinning);
                window.Close();
                return r;
            },
            Ct);

        Assert.True(hiddenReduced);
        Assert.False(shownAgain);
    }

    [Fact]
    public async Task SpinningOrb_WithTurningMotionOff_StopsAndReturnsToRest()
    {
        var (running, angle) = await Session.Dispatch(
            () =>
            {
                var orb = new SpinningOrb { Width = 12, Height = 12, IsActive = true };
                var window = HostOrb(orb, reduce: false);
                Assert.True(orb.IsSpinning);
                Motion.SetReduceMotion(window, true);
                var r = (orb.IsSpinning, orb.Angle);
                window.Close();
                return r;
            },
            Ct);

        Assert.False(running);
        Assert.Equal(0f, angle);
    }

    [Fact]
    public async Task TheBarOrbs_FollowTheRecording_AndHoldStillUnderReducedMotion()
    {
        var (recording, popover, idle, reduced) = await Session.Dispatch(
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
                var barOrb = bar.GetVisualDescendants().OfType<SpinningOrb>().Single();
                var rowOrb = content.GetVisualDescendants().OfType<SpinningOrb>().Single(o => o.IsEffectivelyVisible);
                var r1 = barOrb.IsSpinning;
                var r2 = rowOrb.IsSpinning;

                // The recording ends: both orbs hold still.
                rig.Activity!.Set();
                UiHarness.Pump(4);
                var r3 = !barOrb.IsSpinning;

                // A recording under reduced motion: neither turns. The setting is made on the window only; it reaches the popover's flyout through the visual tree.
                rig.Activity.Set(DesignDemoData.ActivityItems);
                Motion.SetReduceMotion(window, true);
                UiHarness.Pump(4);
                var r4 = !barOrb.IsSpinning && !rowOrb.IsSpinning;
                flyout.Hide();
                window.Close();
                return (r1, r2, r3, r4);
            },
            Ct);

        Assert.True(recording);
        Assert.True(popover);
        Assert.True(idle);
        Assert.True(reduced);
    }

    [Fact]
    public async Task ThePopover_FloatsEightPixelsAboveTheBar()
    {
        var gap = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                using var rig = new DesignRig(statusBar: true);
                var window = UiHarness.Show(rig.Shell);
                var bar = window.GetVisualDescendants().OfType<StatusBarView>().Single();
                var button = bar.FindControl<Button>("ActivityButton")!;
                var flyout = (Flyout)button.Flyout!;
                Click(button);
                var card = ((ActivityPopoverView)flyout.Content!).GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("activityCard"));
                var cardBottom = card.TranslatePoint(new Point(0, card.Bounds.Height), window)!.Value.Y;
                var barTop = bar.TranslatePoint(default, window)!.Value.Y;
                flyout.Hide();
                window.Close();
                return barTop - cardBottom;
            },
            Ct);

        Assert.Equal(8, gap, 0.5);
    }
}
