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

    // ---- the glass orb on the compositor ------------------------------------------------------------------------------

    private sealed class FixedLevel(float value) : ILevelSource
    {
        public float Level { get; set; } = value;
    }

    private static GlassOrbControl NewGlassOrb(bool active, ILevelSource? source = null) =>
        new() { Width = 160, Height = 160, HaloBlur = 18, BodyFraction = 0.77, IsActive = active, LevelSource = source };

    [Fact]
    public void GlassOrb_BreathingCurve_IsTheCanvasEaseInOutOver36Seconds()
    {
        Assert.Equal(0, GlassOrbControl.BreathAt(0), 6);
        Assert.Equal(0.5, GlassOrbControl.BreathAt(0.9), 3);
        Assert.Equal(1, GlassOrbControl.BreathAt(1.8), 6);
        Assert.Equal(0.5, GlassOrbControl.BreathAt(2.7), 3);
        Assert.Equal(0, GlassOrbControl.BreathAt(3.6), 6);
        Assert.Equal(CompositionLoop.Ease(LoopEasing.EaseInOut, 0.25), GlassOrbControl.BreathAt(0.45), 6);
    }

    [Fact]
    public async Task GlassOrb_Breathes_OnTheCompositor_AndFollowsTheVoice_ThroughGlides()
    {
        var (breathing, voice, back, inactive) = await Session.Dispatch(
            () =>
            {
                var orb = NewGlassOrb(active: true);
                var window = Host(orb);
                var a = (orb.IsLooping, orb.IsSampling, orb.IsGliding);

                orb.LevelSource = new FixedLevel(0.5f);
                var b = (orb.IsLooping, orb.IsSampling, orb.IsGliding);

                orb.LevelSource = null;
                var c = (orb.IsLooping, orb.IsSampling, orb.IsGliding);

                orb.IsActive = false;
                var d = (orb.IsLooping, orb.IsSampling, orb.IsGliding, orb.Gate.IsWatching, orb.BodyScale);
                window.Close();
                return (a, b, c, d);
            },
            Ct);

        Assert.Equal((true, false, false), breathing);
        Assert.Equal((false, true, true), voice);
        Assert.Equal((true, false, false), back);
        Assert.Equal((false, false, false, false, 1.0), inactive);
    }

    [Fact]
    public async Task GlassOrb_WithATestClock_RunsNothingOnTheCompositor()
    {
        var state = await Session.Dispatch(
            () =>
            {
                var breathing = NewGlassOrb(active: true);
                var voice = NewGlassOrb(active: true, new FixedLevel(0.5f));
                var window = Host(new StackPanel { Children = { breathing, voice } });
                breathing.Clock = new TestTime(UiHarness.Now);
                voice.Clock = new TestTime(UiHarness.Now);
                var r = (breathing.IsLooping, voice.IsSampling, voice.IsGliding, breathing.IsFrameLoopRunning);
                window.Close();
                return r;
            },
            Ct);

        Assert.Equal((false, false, false, true), state);
    }

    [Fact]
    public async Task GlassOrb_TheCompositorBreathes_WithoutAnyUiWork()
    {
        var moved = await Session.Dispatch(
            () =>
            {
                var orb = NewGlassOrb(active: false);
                var window = Host(orb);
                var rest = UiHarness.Pixels(window);
                orb.IsActive = true;
                UiHarness.Pump(2);
                Thread.Sleep(700);
                UiHarness.Pump(2);
                var breathed = UiHarness.Pixels(window);
                window.Close();
                return !breathed.AsSpan().SequenceEqual(rest);
            },
            Ct);

        Assert.True(moved, "the breathing loops move the orb on the compositor");
    }

    [Fact]
    public async Task GlassOrb_AVoiceSample_HandsTheCompositorASmallGlide_AndSilenceHandsItNothing()
    {
        var (perSample, silent) = await Session.Dispatch(
            () =>
            {
                var level = new FixedLevel(0.1f);
                var orb = NewGlassOrb(active: true, level);
                var window = Host(orb);
                var step = 0;
                long Measure(bool loud)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 100; i++)
                    {
                        step++;
                        level.Level = loud ? ((step & 1) == 0 ? 0.05f : 0.9f) : 0f;
                        orb.Update(TimeSpan.FromMilliseconds(1000 + (100 * step)));
                    }

                    return GC.GetAllocatedBytesForCurrentThread() - before;
                }

                Measure(loud: true);
                var loudBytes = Measure(loud: true);
                Measure(loud: false);
                var quietBytes = Measure(loud: false);
                window.Close();
                return (loudBytes / 100, quietBytes);
            },
            Ct);

        TestContext.Current.TestOutputHelper?.WriteLine($"bytes per loud voice sample (three glides): {perSample}; 100 silent samples: {silent}");
        Assert.InRange(perSample, 1, 2048);
        Assert.Equal(0, silent);
    }

    // ---- the recording orb --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Orb_SamplesAt30Hz_AndGlidesOnTheCompositor_OnlyWhileItMoves()
    {
        var (active, hidden, shown, manual, inactive) = await Session.Dispatch(
            () =>
            {
                var orb = new OrbControl { Width = 120, Height = 120, LevelSource = new FixedLevel(0.4f), IsActive = true };
                var panel = new Border { Child = orb };
                var window = Host(panel);
                var a = (orb.IsFrameLoopRunning, orb.IsGliding);
                panel.IsVisible = false;
                orb.Gate.Check();
                var b = (orb.IsFrameLoopRunning, orb.IsGliding);
                panel.IsVisible = true;
                orb.Gate.Check();
                var c = (orb.IsFrameLoopRunning, orb.IsGliding);
                orb.Clock = new TestTime(UiHarness.Now);
                var d = (orb.IsFrameLoopRunning, orb.IsGliding);
                orb.IsActive = false;
                var e = (orb.IsFrameLoopRunning, orb.IsGliding, orb.Gate.IsWatching);
                window.Close();
                return (a, b, c, d, e);
            },
            Ct);

        Assert.Equal((true, true), active);
        Assert.Equal((false, false), hidden);
        Assert.Equal((true, true), shown);
        Assert.Equal((true, false), manual);
        Assert.Equal((false, false, false), inactive);
    }
}
