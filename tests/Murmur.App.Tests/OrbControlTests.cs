using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Platform;
using Murmur.App.Controls;
using Murmur.App.Tests.Fakes;

namespace Murmur.App.Tests;

public sealed class OrbControlTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private sealed class FixedLevel(float level) : ILevelSource
    {
        public float Level { get; set; } = level;
    }

    private static Window Host(OrbControl orb)
    {
        var window = new Window { Width = 320, Height = 320, Content = orb };
        window.Show();
        UiHarness.Pump();
        return window;
    }

    [Fact]
    public async Task Idle_DrawsAStaticFrame_AndRunsNoTimer()
    {
        var (running, smoothed, scale) = await Session.Dispatch(
            () =>
            {
                var orb = new OrbControl { LevelSource = new FixedLevel(0.9f) };
                var window = Host(orb);
                UiHarness.Pump(10);
                var result = (orb.IsFrameLoopRunning, orb.SmoothedLevel, orb.BodyScale);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.False(running);
        Assert.Equal(0, smoothed);
        Assert.Equal(0.56, scale, 3);
    }

    [Fact]
    public async Task Active_RunsTheFrameLoop_AndFollowsTheLevel()
    {
        var (running, smoothed, scale) = await Session.Dispatch(
            () =>
            {
                var source = new FixedLevel(0.64f);
                var orb = new OrbControl { LevelSource = source, IsActive = true };
                var window = Host(orb);
                orb.Update(TimeSpan.FromSeconds(1000));
                for (var i = 1; i <= 40; i++)
                {
                    orb.Update(TimeSpan.FromSeconds(1000 + (i * 0.05)));
                }

                var result = (orb.IsFrameLoopRunning, orb.SmoothedLevel, orb.BodyScale);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(running);
        Assert.InRange(smoothed, 0.7, 0.85);

        // Louder than the resting 0.56: the breathing is applied through the composition scale.
        Assert.True(scale > 0.56 * 1.1, $"Body scale {scale} should grow with the level.");
    }

    [Theory]
    [InlineData(0f, 0.3)]
    [InlineData(0.5f, 0.3)]
    public async Task SteadyLevel_PushesFarFewerFramesThanUpdates(float level, double maxShare)
    {
        var (updates, applied) = await Session.Dispatch(
            () =>
            {
                var orb = new OrbControl { LevelSource = new FixedLevel(level), IsActive = true };
                var window = Host(orb);
                orb.Update(TimeSpan.FromSeconds(1000));
                var settled = orb.AppliedFrames;

                // 20 s at 60 animation frames per second; the orb itself throttles to its own update rate.
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
        Assert.True(applied < updates * maxShare, $"{applied} frames pushed for {updates} animation frames.");
    }

    [Fact]
    public async Task ChangingLevel_PushesFrames()
    {
        var (applied, scale) = await Session.Dispatch(
            () =>
            {
                var source = new FixedLevel(0f);
                var orb = new OrbControl { LevelSource = source, IsActive = true };
                var window = Host(orb);
                orb.Update(TimeSpan.FromSeconds(1000));
                var before = orb.AppliedFrames;
                source.Level = 1f;
                for (var i = 1; i <= 20; i++)
                {
                    orb.Update(TimeSpan.FromSeconds(1000 + (i * 0.05)));
                }

                var result = (orb.AppliedFrames - before, orb.BodyScale);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(applied >= 5, $"Only {applied} frames were pushed while the level rose.");
        Assert.True(scale > 0.56 * 1.15);
    }

    [Fact]
    public async Task Deactivating_StopsTheFrameLoopAndResetsTheLevel()
    {
        var (running, smoothed) = await Session.Dispatch(
            () =>
            {
                var orb = new OrbControl { LevelSource = new FixedLevel(1f), IsActive = true };
                var window = Host(orb);
                UiHarness.Pump(5);
                orb.IsActive = false;
                UiHarness.Pump(5);
                var result = (orb.IsFrameLoopRunning, orb.SmoothedLevel);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.False(running);
        Assert.Equal(0, smoothed);
    }

    [Fact]
    public async Task ReplacingTheClock_RestartsTheAnimation_SoEarlierTicksLeaveNoTrace()
    {
        var (clean, afterStrayTicks) = await Session.Dispatch(
            () => (BreatheOnAManualClock(strayTicks: 0), BreatheOnAManualClock(strayTicks: 5)),
            TestContext.Current.CancellationToken);

        Assert.Equal(clean, afterStrayTicks);
    }

    [Fact]
    public async Task Ticks_WithoutEnoughTimePassing_ChangeNothing()
    {
        var (framesBefore, framesAfter, scaleBefore, scaleAfter) = await Session.Dispatch(
            () =>
            {
                var source = new FixedLevel(0.3f);
                var orb = new OrbControl { LevelSource = source, IsActive = true };
                var window = Host(orb);
                var clock = new TestTime(UiHarness.Now);
                orb.Clock = clock;
                orb.Tick();
                Breathe(clock, orb, steps: 10);
                var frames = orb.AppliedFrames;
                var scale = orb.BodyScale;

                // What the 15 Hz timer does when the clock has not moved on by a whole update interval: nothing,
                // even if the level has changed meanwhile.
                source.Level = 1f;
                for (var i = 0; i < 10; i++)
                {
                    orb.Tick();
                }

                clock.Advance(TimeSpan.FromMilliseconds(60));
                orb.Tick();
                var result = (frames, orb.AppliedFrames, scale, orb.BodyScale);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(framesBefore, framesAfter);
        Assert.Equal(scaleBefore, scaleAfter);
    }

    [Fact]
    public async Task EveryStart_BeginsWithTheBreathAtRest()
    {
        var (first, restarted, reattached) = await Session.Dispatch(
            () =>
            {
                var orb = new OrbControl { LevelSource = new FixedLevel(0.64f), IsActive = true };
                var window = Host(orb);
                var clock = new TestTime(UiHarness.Now);
                orb.Clock = clock;
                orb.Tick();
                var atStart = orb.BodyScale;

                // Breathe for a while, then stop and start again (a new recording).
                Breathe(clock, orb, steps: 15);
                orb.IsActive = false;
                orb.IsActive = true;
                orb.Tick();
                var afterRestart = orb.BodyScale;

                // Breathe again, then leave the visual tree and come back (navigating away and back while recording).
                Breathe(clock, orb, steps: 15);
                window.Content = null;
                UiHarness.Pump();
                window.Content = orb;
                UiHarness.Pump();
                orb.Tick();
                var afterReattach = orb.BodyScale;

                window.Close();
                return (atStart, afterRestart, afterReattach);
            },
            TestContext.Current.CancellationToken);

        var atRest = 0.56 * (1 + (Math.Sqrt(0.64f) * 0.22));
        Assert.Equal(atRest, first, 9);
        Assert.Equal(atRest, restarted, 9);
        Assert.Equal(atRest, reattached, 9);
    }

    [Fact]
    public async Task LayoutPass_DuringARecording_KeepsTheCurrentFrame()
    {
        var (before, after, arranged) = await Session.Dispatch(
            () =>
            {
                var orb = new OrbControl { LevelSource = new FixedLevel(0.64f), IsActive = true };
                var window = Host(orb);
                var clock = new TestTime(UiHarness.Now);
                orb.Clock = clock;
                orb.Tick();

                // About 0.84 s in: the breath is far from rest.
                Breathe(clock, orb, steps: 12);
                var scale = orb.BodyScale;
                var frames = orb.AppliedFrames;
                orb.InvalidateArrange();
                UiHarness.Pump();
                var result = (scale, orb.BodyScale, orb.AppliedFrames > frames);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(arranged, "The orb should have been arranged again.");
        Assert.NotEqual(0.56 * (1 + (Math.Sqrt(0.64f) * 0.22)), before, 6);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task NaNLevel_CountsAsSilence()
    {
        var (smoothed, scale) = await Session.Dispatch(
            () =>
            {
                var orb = new OrbControl { LevelSource = new FixedLevel(float.NaN), IsActive = true };
                var window = Host(orb);
                var clock = new TestTime(UiHarness.Now);
                orb.Clock = clock;
                orb.Tick();
                Breathe(clock, orb, steps: 5);
                var result = (orb.SmoothedLevel, orb.BodyScale);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, smoothed);
        Assert.InRange(scale, 0.55, 0.57);
    }

    [Theory]
    [InlineData(-0.5f, 0.0)]
    [InlineData(float.NegativeInfinity, 0.0)]
    [InlineData(7f, 1.0)]
    public async Task OutOfRangeLevels_AreClampedIntoTheUnitRange(float level, double expected)
    {
        var smoothed = await Session.Dispatch(
            () =>
            {
                var orb = new OrbControl { LevelSource = new FixedLevel(level), IsActive = true };
                var window = Host(orb);
                var clock = new TestTime(UiHarness.Now);
                orb.Clock = clock;
                orb.Tick();
                var result = orb.SmoothedLevel;
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, smoothed, 9);
    }

    [Fact]
    public async Task Orb_ResolvesItsColoursFromTheTokenResources_AndPaintsThem()
    {
        var (violetSeen, emberSeen) = await Session.Dispatch(
            () =>
            {
                var orb = new OrbControl { IsActive = false };
                var window = Host(orb);
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);

                // Probe pixels: the core is violet-ish (blue dominant), the rim is ember-ish (red dominant).
                var centre = Probe(frame, 160, 160);
                var rim = Probe(frame, 160 + 78, 160 + 40);
                window.Close();
                return (centre.B > centre.R, rim.R > rim.B);
            },
            TestContext.Current.CancellationToken);

        Assert.True(violetSeen, "Orb core should be violet.");
        Assert.True(emberSeen, "Orb rim should be ember.");
    }

    /// <summary>Advances <paramref name="clock"/> by one 70 ms timer interval per step and ticks the orb.</summary>
    private static void Breathe(TestTime clock, OrbControl orb, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(70));
            orb.Tick();
        }
    }

    /// <summary>
    /// <paramref name="strayTicks"/> real-time ticks (system clock, uneven moments) first, then a manual clock: quiet,
    /// a loud passage and a release, so smoothing, breathing and the skip thresholds all take part.
    /// </summary>
    private static (double Scale, double Opacity, double Smoothed, long Frames) BreatheOnAManualClock(int strayTicks)
    {
        var source = new FixedLevel(0.05f);
        var orb = new OrbControl { LevelSource = source, IsActive = true };
        var window = Host(orb);
        for (var i = 0; i < strayTicks; i++)
        {
            Thread.Sleep(70 + (i * 11));
            source.Level = 0.1f * (i + 1);
            orb.Tick();
        }

        source.Level = 0.05f;
        var clock = new TestTime(UiHarness.Now);
        orb.Clock = clock;
        var framesAtStart = orb.AppliedFrames;
        orb.Tick();
        Breathe(clock, orb, steps: 10);
        source.Level = 0.9f;
        Breathe(clock, orb, steps: 10);
        source.Level = 0.2f;
        Breathe(clock, orb, steps: 10);
        var result = (orb.BodyScale, orb.GlowOpacity, orb.SmoothedLevel, orb.AppliedFrames - framesAtStart);
        window.Close();
        return result;
    }

    private static (byte R, byte G, byte B) Probe(Avalonia.Media.Imaging.WriteableBitmap frame, int x, int y)
    {
        using var buffer = frame.Lock();
        var bytesPerPixel = 4;
        var offset = (y * buffer.RowBytes) + (x * bytesPerPixel);
        var pixel = new byte[bytesPerPixel];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + offset, pixel, 0, bytesPerPixel);

        // Headless Skia frames are BGRA or RGBA depending on the platform; Format tells which.
        return buffer.Format == PixelFormat.Bgra8888 ? (pixel[2], pixel[1], pixel[0]) : (pixel[0], pixel[1], pixel[2]);
    }
}
