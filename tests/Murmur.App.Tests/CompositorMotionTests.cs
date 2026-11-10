using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Murmur.App.Controls;
using Murmur.App.Tests.Fakes;

namespace Murmur.App.Tests;

/// <summary>
/// The motion that runs on the compositor (#99): the shape of the loops, the gate that starts and stops them (active, attached,
/// seen, motion allowed), and that a running loop really moves the picture at the display rate without the UI thread.
/// </summary>
public sealed class CompositorMotionTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Window Host(Control content, bool reduce = false)
    {
        var window = new Window { Width = 200, Height = 200, Content = content, Background = Brushes.White };
        Motion.SetReduceMotion(window, reduce);
        window.Show();
        UiHarness.Pump(4);
        return window;
    }

    // ---- the loop shapes ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    public void Ease_IsPinnedAtTheEndsAndSymmetricInTheMiddle(double t, double expected)
    {
        Assert.Equal(expected, CompositionLoop.Ease(LoopEasing.EaseInOut, t), 4);
        Assert.Equal(expected, CompositionLoop.Ease(LoopEasing.Linear, t), 4);
    }

    [Fact]
    public void CssEaseInOut_MatchesTheBrowsersCurve()
    {
        // cubic-bezier(0.42, 0, 0.58, 1) at x = 0.25 is 0.1291 (the value Chromium uses for ease-in-out).
        Assert.Equal(0.1291, CompositionLoop.Ease(LoopEasing.EaseInOut, 0.25), 3);
        Assert.Equal(1 - 0.1291, CompositionLoop.Ease(LoopEasing.EaseInOut, 0.75), 3);
        Assert.True(CompositionLoop.Ease(LoopEasing.EaseOut, 0.25) > 0.25, "ease-out is ahead of linear at the start");
    }

    [Fact]
    public void Sample_FollowsTheKeys_AndWrapsEveryPeriod()
    {
        LoopKey[] keys = [new(0, 1), new(0.5f, 2, LoopEasing.EaseInOut), new(1, 1, LoopEasing.EaseInOut)];

        Assert.Equal(1, CompositionLoop.Sample(keys, 0), 6);
        Assert.Equal(2, CompositionLoop.Sample(keys, 0.5), 6);
        Assert.Equal(1.5, CompositionLoop.Sample(keys, 0.25), 6);
        Assert.Equal(CompositionLoop.Sample(keys, 0.3), CompositionLoop.Sample(keys, 7.3), 6);
        Assert.Equal(1, CompositionLoop.Sample(keys, 1), 6);
    }

    [Fact]
    public void Spin_TurnsLinearlyOncePerNineSeconds()
    {
        Assert.Equal(0, SpinningOrb.AngleAt(0), 6);
        Assert.Equal(Math.PI / 2, SpinningOrb.AngleAt(2.25), 6);
        Assert.Equal(Math.PI, SpinningOrb.AngleAt(4.5), 6);
        Assert.Equal(0, SpinningOrb.AngleAt(9), 6);
        Assert.Equal(SpinningOrb.AngleAt(1), SpinningOrb.AngleAt(10), 6);
    }

    // ---- the spinning orb on the compositor ---------------------------------------------------------------------------

    [Fact]
    public async Task SpinningOrb_StartsItsLoopWhenActive_AndEndsItWhenInactive()
    {
        var (idle, active, stopped, angle) = await Session.Dispatch(
            () =>
            {
                var orb = new SpinningOrb { Width = 12, Height = 12 };
                var window = Host(orb);
                var before = orb.IsLooping;
                orb.IsActive = true;
                UiHarness.Pump(2);
                var running = orb.IsLooping;
                orb.IsActive = false;
                var r = (before, running, !orb.IsLooping, orb.Angle);
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
    public async Task SpinningOrb_DetachedOrReduced_RunsNoLoop_AndStartsAgainWhenBack()
    {
        var (detached, reattached, reduced, allowed) = await Session.Dispatch(
            () =>
            {
                var orb = new SpinningOrb { Width = 12, Height = 12, IsActive = true };
                var window = Host(orb);
                window.Content = null;
                UiHarness.Pump(2);
                var a = orb.IsLooping || orb.Gate.IsWatching;
                window.Content = orb;
                UiHarness.Pump(2);
                var b = orb.IsLooping;
                Motion.SetReduceMotion(window, true);
                var c = orb.IsLooping || orb.Gate.IsWatching;
                Motion.SetReduceMotion(window, false);
                var r = (a, b, c, orb.IsLooping);
                window.Close();
                return r;
            },
            Ct);

        Assert.False(detached);
        Assert.True(reattached);
        Assert.False(reduced);
        Assert.True(allowed);
    }

    [Fact]
    public async Task SpinningOrb_HiddenByItsParentOrAMinimizedWindow_StopsAtTheNextCheck_AndTurnsAgainWhenSeen()
    {
        var (shown, hidden, again, minimized, restored) = await Session.Dispatch(
            () =>
            {
                var orb = new SpinningOrb { Width = 12, Height = 12, IsActive = true };
                var panel = new Border { Child = orb };
                var window = Host(panel);
                var a = orb.IsLooping;

                // The parent hides: no event reaches the orb, the slow check notices.
                panel.IsVisible = false;
                orb.Gate.Check();
                var b = orb.IsLooping;
                panel.IsVisible = true;
                orb.Gate.Check();
                var c = orb.IsLooping;

                window.WindowState = WindowState.Minimized;
                orb.Gate.Check();
                var d = orb.IsLooping;
                window.WindowState = WindowState.Normal;
                orb.Gate.Check();
                var r = (a, b, c, d, orb.IsLooping);
                window.Close();
                return r;
            },
            Ct);

        Assert.True(shown);
        Assert.False(hidden);
        Assert.True(again);
        Assert.False(minimized);
        Assert.True(restored);
    }

    [Fact]
    public async Task SpinningOrb_Idle_WatchesNothing()
    {
        var watching = await Session.Dispatch(
            () =>
            {
                var orb = new SpinningOrb { Width = 12, Height = 12 };
                var window = Host(orb);
                var r = orb.Gate.IsWatching || orb.IsLooping;
                window.Close();
                return r;
            },
            Ct);

        Assert.False(watching);
    }

    [Fact]
    public async Task SpinningOrb_WithATestClock_RunsNoLoop_AndDrawsTheClocksAngle()
    {
        var (looping, angle) = await Session.Dispatch(
            () =>
            {
                var orb = new SpinningOrb { Width = 12, Height = 12, IsActive = true };
                var window = Host(orb);
                var clock = new TestTime(UiHarness.Now);
                orb.Clock = clock;
                clock.Advance(TimeSpan.FromSeconds(2.25));
                orb.Tick();
                var r = (orb.IsLooping, orb.Angle);
                window.Close();
                return r;
            },
            Ct);

        Assert.False(looping);
        Assert.Equal(MathF.PI / 2, angle!.Value, 0.01f);
    }

    [Fact]
    public async Task SpinningOrb_TheCompositorTurnsThePicture_WithoutAnyUiWork()
    {
        var (moved, restAgain) = await Session.Dispatch(
            () =>
            {
                var orb = new SpinningOrb { Width = 160, Height = 160 };
                var window = Host(orb);
                var rest = UiHarness.Pixels(window);
                orb.IsActive = true;
                UiHarness.Pump(2);

                // Real time passes on the render clock; the UI thread only lets the headless renderer draw.
                Thread.Sleep(600);
                UiHarness.Pump(2);
                var turned = UiHarness.Pixels(window);
                orb.IsActive = false;
                UiHarness.Pump(2);
                var after = UiHarness.Pixels(window);
                window.Close();
                return (!turned.AsSpan().SequenceEqual(rest), after.AsSpan().SequenceEqual(rest));
            },
            Ct);

        Assert.True(moved, "a running loop turns the orb on the compositor");
        Assert.True(restAgain, "a stopped orb is back at angle 0");
    }
}
