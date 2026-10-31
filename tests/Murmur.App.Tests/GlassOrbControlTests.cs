using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Murmur.App.Controls;
using Murmur.App.Tests.Fakes;

namespace Murmur.App.Tests;

/// <summary>The orb of the onboarding canvas: its layout, the animation loop, frame skipping and the cached halo.</summary>
public sealed class GlassOrbControlTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private sealed class FixedLevel(float level) : ILevelSource
    {
        public float Level { get; set; } = level;
    }

    private static GlassOrbControl NewOrb(bool active = false, ILevelSource? source = null) =>
        new() { Width = 300, Height = 300, HaloBlur = 30, BodyFraction = 0.77, IsActive = active, LevelSource = source };

    private static Window Host(Control orb)
    {
        var window = new Window { Width = 500, Height = 500, Content = orb };
        window.Show();
        UiHarness.Pump();
        return window;
    }

    /// <summary>Half a breathing period in steps of 0.1 s: the breath is at its peak.</summary>
    private static void BreatheToThePeak(GlassOrbControl orb)
    {
        for (var i = 1; i <= 18; i++)
        {
            orb.Update(TimeSpan.FromSeconds(0.1 * i));
        }
    }

    [Fact]
    public async Task Halo_ReachesPastTheOrb_SoTheBlurIsNotClipped()
    {
        var bounds = await Session.Dispatch(
            () =>
            {
                var orb = NewOrb();
                var window = Host(orb);
                var result = orb.HaloBounds;
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(-90, bounds.X);
        Assert.Equal(-90, bounds.Y);
        Assert.Equal(480, bounds.Width);
        Assert.Equal(480, bounds.Height);
    }

    [Fact]
    public async Task Idle_ShowsOneStaticFrame_AndRunsNoTimer()
    {
        var (running, body, halo) = await Session.Dispatch(
            () =>
            {
                var orb = NewOrb();
                var window = Host(orb);
                UiHarness.Pump(10);
                var result = (orb.IsFrameLoopRunning, orb.BodyScale, orb.HaloScale);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.False(running);
        Assert.Equal(1.0, body, 3);
        Assert.Equal(1.0, halo, 3);
    }

    [Fact]
    public async Task Active_RunsTheFrameLoop_UntilItIsDeactivated()
    {
        var (whileActive, afterwards) = await Session.Dispatch(
            () =>
            {
                var orb = NewOrb(active: true);
                var window = Host(orb);
                var running = orb.IsFrameLoopRunning;
                orb.IsActive = false;
                UiHarness.Pump(5);
                var result = (running, orb.IsFrameLoopRunning);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(whileActive);
        Assert.False(afterwards);
    }

    [Fact]
    public async Task Detaching_StopsTheFrameLoop_AndAttachingAgainStartsIt()
    {
        var (attached, detached, reattached) = await Session.Dispatch(
            () =>
            {
                var orb = NewOrb(active: true);
                var window = Host(orb);
                var first = orb.IsFrameLoopRunning;
                window.Content = null;
                UiHarness.Pump(5);
                var second = orb.IsFrameLoopRunning;
                window.Content = orb;
                UiHarness.Pump(5);
                var result = (first, second, orb.IsFrameLoopRunning);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(attached);
        Assert.False(detached);
        Assert.True(reattached);
    }

    [Fact]
    public async Task WhileHiddenOrMinimized_TheTimerOnlyWakesUpToNoticeTheChange()
    {
        var (shown, hidden, shownAgain, minimized) = await Session.Dispatch(
            () =>
            {
                var orb = NewOrb(active: true);
                var window = Host(orb);
                orb.OnTimer();
                var interval = orb.TimerInterval;
                var framesBefore = orb.AppliedFrames;

                orb.IsVisible = false;
                orb.OnTimer();
                var rest = (orb.TimerInterval, orb.AppliedFrames - framesBefore);

                orb.IsVisible = true;
                orb.OnTimer();
                var again = orb.TimerInterval;

                window.WindowState = WindowState.Minimized;
                orb.OnTimer();
                var minimizedInterval = orb.TimerInterval;
                window.Close();
                return (interval, rest, again, minimizedInterval);
            },
            TestContext.Current.CancellationToken);

        Assert.True(shown < TimeSpan.FromMilliseconds(100));
        Assert.True(hidden.Item1 >= TimeSpan.FromMilliseconds(250), "a hidden orb must not tick at the animation rate");
        Assert.Equal(0, hidden.Item2);
        Assert.Equal(shown, shownAgain);
        Assert.True(minimized >= TimeSpan.FromMilliseconds(250), "a minimized window must not animate");
    }

    [Fact]
    public async Task Breathing_GrowsTheSphereByFivePercentAndTheHaloBySixteen_AtThePeak()
    {
        var (start, peak) = await Session.Dispatch(
            () =>
            {
                var orb = NewOrb(active: true);
                var window = Host(orb);
                orb.Clock = new TestTime(UiHarness.Now);
                orb.Tick();
                var first = (orb.BodyScale, orb.HaloScale);
                BreatheToThePeak(orb);
                var result = (first, (orb.BodyScale, orb.HaloScale));
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1.0, start.BodyScale, 3);
        Assert.Equal(1.05, peak.BodyScale, 2);
        Assert.Equal(1.16, peak.HaloScale, 2);
    }

    [Fact]
    public async Task WithALevelSource_TheSphereFollowsTheVoice()
    {
        var (quiet, loud) = await Session.Dispatch(
            () =>
            {
                var source = new FixedLevel(0f);
                var orb = NewOrb(active: true, source);
                var window = Host(orb);
                orb.Clock = new TestTime(UiHarness.Now);
                orb.Update(TimeSpan.FromSeconds(100));
                var calm = orb.BodyScale;
                source.Level = 1f;
                for (var i = 1; i <= 40; i++)
                {
                    orb.Update(TimeSpan.FromSeconds(100 + (i * 0.1)));
                }

                var result = (calm, orb.BodyScale);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1.0, quiet, 3);
        Assert.True(loud > 1.08, $"the sphere grew to {loud}");
    }

    [Fact]
    public async Task Updates_ComingFasterThanTheUpdateRate_PushNoFrames()
    {
        var (framesBefore, framesAfter) = await Session.Dispatch(
            () =>
            {
                var orb = NewOrb(active: true);
                var window = Host(orb);
                orb.Clock = new TestTime(UiHarness.Now);
                orb.Tick();
                orb.Update(TimeSpan.FromSeconds(1.0));
                var before = orb.AppliedFrames;

                // Less than 80 % of one update interval later, and again: nothing is redrawn.
                orb.Update(TimeSpan.FromSeconds(1.0 + 0.04));
                orb.Update(TimeSpan.FromSeconds(1.0 + 0.05));
                var result = (before, orb.AppliedFrames);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(framesBefore, framesAfter);
    }

    [Fact]
    public async Task SixtyUpdatesPerSecond_PushFarFewerFrames_ThanTheyAre()
    {
        var (updates, applied) = await Session.Dispatch(
            () =>
            {
                var orb = NewOrb(active: true);
                var window = Host(orb);
                orb.Clock = new TestTime(UiHarness.Now);
                orb.Tick();
                var settled = orb.AppliedFrames;
                var count = 0;
                for (var t = 1000.0; t < 1020.0; t += 1.0 / 60)
                {
                    orb.Update(TimeSpan.FromSeconds(t));
                    count++;
                }

                var result = (count, orb.AppliedFrames - settled);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(applied > 0, "The breathing should still be visible.");
        Assert.True(applied < updates * 0.3, $"{applied} frames pushed for {updates} animation frames.");
    }

    [Fact]
    public async Task TheHalo_IsComputedOncePerSize_NotOnEveryFrame()
    {
        var (first, afterAnimation, afterResize) = await Session.Dispatch(
            () =>
            {
                var orb = NewOrb(active: true);
                var window = Host(orb);
                var built = orb.HaloBuilds;

                orb.Clock = new TestTime(UiHarness.Now);
                orb.Tick();
                BreatheToThePeak(orb);
                UiHarness.Pump(5);
                var animated = orb.HaloBuilds;

                orb.Width = 200;
                orb.Height = 200;
                UiHarness.Pump(5);
                var result = (built, animated, orb.HaloBuilds);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, first);
        Assert.Equal(1, afterAnimation);
        Assert.Equal(2, afterResize);
    }

    [Fact]
    public async Task TheHalo_IsRebuiltExactlyOnce_WhenTheThemeChangesItsBlur_AndNeverForTheOtherLookChanges()
    {
        var (first, dark, sameTheme, opacities, glow, back) = await Session.Dispatch(
            () =>
            {
                // The welcome class takes HaloBlur from the themed Glass.Welcome.HaloBlur (30 light, 34 dark).
                var orb = new GlassOrbControl();
                orb.Classes.Add("welcome");
                var window = Host(orb);
                var built = orb.HaloBuilds;

                window.RequestedThemeVariant = ThemeVariant.Dark;
                UiHarness.Pump(5);
                var toDark = orb.HaloBuilds;

                window.RequestedThemeVariant = ThemeVariant.Dark;
                UiHarness.Pump(5);
                var same = orb.HaloBuilds;

                orb.HaloOpacity = 0.5;
                UiHarness.Pump(5);
                orb.InnerGlowOpacity = 0.1;
                UiHarness.Pump(5);
                var afterOpacities = orb.HaloBuilds;

                orb.InnerGlowBlur = 12;
                UiHarness.Pump(5);
                var afterGlow = orb.HaloBuilds;

                window.RequestedThemeVariant = ThemeVariant.Light;
                UiHarness.Pump(5);
                var result = (built, toDark, same, afterOpacities, afterGlow, orb.HaloBuilds);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, first);
        Assert.Equal(2, dark);
        Assert.Equal(2, sameTheme);
        Assert.Equal(2, opacities);
        Assert.Equal(2, glow);
        Assert.Equal(3, back);
    }

    [Fact]
    public async Task WithoutTheGradientResources_TheOrbPaintsNothing_AndDoesNotThrow()
    {
        var built = await Session.Dispatch(
            () =>
            {
                var orb = NewOrb();
                orb.Resources["Orb.Base"] = "not a colour";
                var window = Host(orb);
                UiHarness.Pump(5);
                var result = orb.HaloBuilds;
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, built);
    }

    [Fact]
    public async Task Detaching_ReleasesTheHaloBitmap_AndAttachingAgainBuildsItAnew()
    {
        var (attached, detached, reattached, builds) = await Session.Dispatch(
            () =>
            {
                var orb = NewOrb();
                var window = Host(orb);
                var first = orb.HasHaloBitmap;

                window.Content = null;
                UiHarness.Pump(5);
                var second = orb.HasHaloBitmap;

                window.Content = orb;
                UiHarness.Pump(5);
                var result = (first, second, orb.HasHaloBitmap, orb.HaloBuilds);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(attached);
        Assert.False(detached);
        Assert.True(reattached);
        Assert.Equal(2, builds);
    }

    [Fact]
    public async Task TheAnimationSteps_AllocateNothing_OnceWarm()
    {
        var attempts = await Session.Dispatch(
            () =>
            {
                var level = new FixedLevel(0.2f);
                var breathing = NewOrb(active: true);
                var voice = NewOrb(active: true, source: level);
                var window = new Window { Width = 700, Height = 400, Content = new StackPanel { Children = { breathing, voice } } };
                window.Show();
                UiHarness.Pump();

                var step = 0;
                void Pass()
                {
                    for (var i = 0; i < 100; i++)
                    {
                        step++;
                        level.Level = (step & 1) == 0 ? 0.1f : 0.6f;
                        var at = TimeSpan.FromMilliseconds(100 * step);
                        breathing.Update(at);
                        voice.Update(at);
                    }
                }

                // Warm up first; a pass that allocates nothing proves the steady state, while one-time runtime work (tiered JIT) can land
                // in any single pass.
                Pass();
                var bytes = new List<long>();
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    Pass();
                    bytes.Add(GC.GetAllocatedBytesForCurrentThread() - before);
                    if (bytes[^1] == 0)
                    {
                        break;
                    }
                }

                window.Close();
                return bytes;
            },
            TestContext.Current.CancellationToken);

        Assert.True(attempts[^1] == 0, $"bytes allocated per pass of 100 steps: {string.Join(", ", attempts)}");
    }

    [Fact]
    public async Task WithoutAHalo_NothingIsComputed()
    {
        var built = await Session.Dispatch(
            () =>
            {
                var orb = new GlassOrbControl { Width = 18, Height = 18 };
                var window = Host(orb);
                var result = orb.HaloBuilds;
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, built);
    }
}
