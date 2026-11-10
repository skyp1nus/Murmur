using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Murmur.App.Controls;

namespace Murmur.App.Tests;

/// <summary>
/// The level bars that scroll on the render thread (#99): the pure slide maths (offsets between samples, the newest bar easing in,
/// bursts and motion off at rest), that a frame allocates nothing, and that the controls wake the render loop only while they slide.
/// </summary>
public sealed class LevelBarsTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LevelBarsStyle History = LevelHistoryControl.Style;

    private static readonly LevelBarsStyle Meter = LevelMeterControl.Style;

    private static LevelBarsMotion Filled(LevelBarsStyle style)
    {
        var motion = new LevelBarsMotion(style);
        var values = new float[style.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (i % 5) / 4f;
        }

        motion.Reset(values, now: 0);
        return motion;
    }

    // ---- the slide ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ASteadySample_SlidesTheStripOneBarOverOneInterval_AtConstantSpeed()
    {
        var motion = Filled(History);
        motion.Push(0.5f, now: 0.25, animate: true);

        var slide = 0.25 * LevelBarsMotion.SlideSlack;
        Assert.Equal(0, motion.Position(0.25), 6);
        Assert.Equal(0.5, motion.Position(0.25 + (slide / 2)), 6);
        Assert.Equal(1, motion.Position(0.25 + slide), 6);
        Assert.Equal(1, motion.Position(9), 6);
        Assert.True(motion.IsMoving(0.4));
        Assert.False(motion.IsMoving(0.25 + slide + 1e-9));
    }

    [Fact]
    public void ASampleLateByTimerJitter_FindsTheStripStillMoving_AndASteadyStreamStaysAFixedBitBehind()
    {
        var motion = Filled(History);
        var t = 0.25;
        motion.Push(0.5f, t, animate: true);

        // 10 ms late (4 %): the strip has not stopped before the sample arrives.
        Assert.True(motion.IsMoving(t + 0.26));

        // A long steady stream neither pauses nor drifts: right before each sample the strip is 5 % of a bar from rest.
        var lowest = 1.0;
        var highest = 0.0;
        for (var i = 0; i < 200; i++)
        {
            var next = t + 0.25;
            Assert.True(motion.IsMoving(next - 1e-6), $"paused before sample {i}");
            lowest = Math.Min(lowest, motion.Position(next));
            highest = Math.Max(highest, motion.Position(next));
            motion.Push((i % 3) / 2f, next, animate: true);
            t = next;
        }

        Assert.InRange(lowest, 0.9, 1);
        Assert.InRange(highest, 0.9, 1);
        Assert.Equal(1 - (LevelBarsMotion.SlideSlack - 1), motion.Position(t + 0.25), 3);
    }

    [Fact]
    public void TheShift_KeepsEveryBarWhereItWas_AndTheRestFrameIsTheStaticStrip()
    {
        var motion = Filled(History);
        var before = new LevelBar[History.MaxRects];
        var after = new LevelBar[History.MaxRects];
        var n = motion.Layout(before, 360, 36, position: 1, idle: false);

        motion.Push(0.9f, now: 0.25, animate: true);
        var m = motion.Layout(after, 360, 36, motion.Position(0.25), idle: false);

        // The stubs come first in both frames; then the bars. Right after the sample each old bar is where it was.
        var stubs = History.FutureCount;
        for (var i = 0; i < History.Count; i++)
        {
            Assert.Equal(before[stubs + i].X, after[stubs + i].X, 6);
            Assert.Equal(before[stubs + i].Height, after[stubs + i].Height, 6);
        }

        // The new bar sits on the first stub, not yet seen; at rest it is the last past bar, fully there.
        Assert.Equal(m, n + 1);
        var arriving = after[m - 1];
        Assert.Equal(before[0].X, arriving.X, 6);
        Assert.Equal(0, arriving.Opacity, 6);
        Assert.Equal(History.MinHeight, arriving.Height, 6);

        var rest = new LevelBar[History.MaxRects];
        var r = motion.Layout(rest, 360, 36, motion.Position(1), idle: false);
        Assert.Equal(n, r);
        Assert.Equal(before[stubs + History.Count - 1].X, rest[r - 1].X, 6);
        Assert.Equal(1, rest[r - 1].Opacity, 6);
        Assert.Equal(LevelHistoryControl.BarHeight(0.9f), rest[r - 1].Height, 6);
    }

    [Fact]
    public void TheRestFrame_MatchesTheCanvasStrip()
    {
        // 36 bars, 3 px apart in 360 px: (360 - 105) / 36 px each; past bars 4..32 px, centred; 10 stubs of 4 px.
        var motion = Filled(History);
        var bars = new LevelBar[History.MaxRects];
        var count = motion.Layout(bars, 360, 36, position: 1, idle: false);
        var width = (360.0 - 105) / 36;

        Assert.Equal(36, count);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(bars[i].Second);
            Assert.Equal((26 + i) * (width + 3), bars[i].X, 6);
            Assert.Equal(4, bars[i].Height);
        }

        for (var i = 0; i < 26; i++)
        {
            var bar = bars[10 + i];
            Assert.False(bar.Second);
            Assert.Equal(i * (width + 3), bar.X, 6);
            Assert.Equal(width, bar.Width, 6);
            Assert.Equal(width / 2, bar.Radius, 6);
            Assert.Equal((36 - bar.Height) / 2, bar.Y, 6);
            Assert.Equal(LevelHistoryControl.BarHeight((i % 5) / 4f), bar.Height);
        }
    }

    [Fact]
    public void AnIdleTrack_IsStubsOnly()
    {
        var bars = new LevelBar[History.MaxRects];
        var count = Filled(History).Layout(bars, 360, 36, position: 1, idle: true);

        Assert.Equal(36, count);
        Assert.All(bars[..count], b => Assert.True(b.Second && b.Height == 4));
    }

    [Fact]
    public void ABurst_MotionOff_OrAFlatStrip_ShowTheNewValuesAtRestAtOnce()
    {
        var burst = Filled(History);
        burst.Push(0.3f, now: 0.25, animate: true);
        burst.Push(0.6f, now: 0.25, animate: true);
        Assert.Equal(1, burst.Position(0.25), 6);
        Assert.Equal(0.6f, burst.Values[^1]);

        var reduced = Filled(History);
        reduced.Push(0.3f, now: 0.25, animate: false);
        Assert.False(reduced.IsMoving(0.25));

        var flat = new LevelBarsMotion(History);
        flat.Reset(new float[History.Count], now: 0);
        flat.Push(0f, now: 0.25, animate: true);
        Assert.False(flat.IsMoving(0.25), "a flat strip looks the same however far it slides");
    }

    [Fact]
    public void AnEarlySample_KeepsTheDistanceStillToGo_SoNothingJumpsBack()
    {
        var motion = Filled(History);
        motion.Push(0.5f, now: 0.25, animate: true);

        // Half-way through the slide the next sample comes (a short interval, not a burst): the strip stays where it was on screen.
        var before = motion.Position(0.375);
        motion.Push(0.7f, now: 0.375, animate: true);
        Assert.Equal(before - 1, motion.Position(0.375), 6);
        Assert.True(motion.Position(0.5) > before - 1);

        // Never more than one bar behind.
        motion.Push(0.1f, now: 0.48, animate: true);
        Assert.True(motion.Position(0.48) >= -1);
    }

    [Fact]
    public void TheSlideFollowsTheRealInterval()
    {
        var motion = Filled(History);
        var t = 0.0;
        for (var i = 0; i < 40; i++)
        {
            t += 0.3;
            motion.Push((i % 3) / 2f, t, animate: true);
        }

        Assert.Equal(0.3, motion.PeriodSeconds, 2);
    }

    [Fact]
    public void Arrival_EasesFromZeroToOne()
    {
        Assert.Equal(0, LevelBarsMotion.Arrival(-0.5), 6);
        Assert.Equal(0, LevelBarsMotion.Arrival(0), 6);
        Assert.Equal(0.5, LevelBarsMotion.Arrival(0.5), 6);
        Assert.Equal(1, LevelBarsMotion.Arrival(1), 6);
    }

    [Fact]
    public void TheMeter_SlidesItsNewestBarInFromBeyondTheRightEdge()
    {
        var motion = Filled(Meter);
        var bars = new LevelBar[Meter.MaxRects];
        motion.Push(0.8f, now: 0.05, animate: true);

        var count = motion.Layout(bars, 200, 24, motion.Position(0.05), idle: false);
        Assert.Equal(Meter.Count, count);
        Assert.True(bars[count - 1].X + bars[count - 1].Width <= 200 + 1e-9);

        var half = motion.Layout(bars, 200, 24, motion.Position(0.075), idle: false);
        Assert.Equal(Meter.Count + 1, half);
        Assert.True(bars[half - 1].X < 200 && bars[half - 1].X + bars[half - 1].Width > 200, "the newest bar is cut by the edge");
        Assert.Equal(1, bars[half - 1].Opacity, 6);
    }

    [Fact]
    public void TheMeterBars_AreIdleStubsForSilence_AndAtLeastFourPixelsWhenLive()
    {
        var motion = new LevelBarsMotion(Meter);
        var values = new float[Meter.Count];
        values[^1] = 0.01f;
        values[^2] = 0.5f;
        motion.Reset(values, now: 0);
        var bars = new LevelBar[Meter.MaxRects];
        var count = motion.Layout(bars, 200, 24, position: 1, idle: false);

        Assert.True(bars[0].Second);
        Assert.Equal(3, bars[0].Height);
        Assert.Equal(4, bars[count - 1].Height);
        Assert.Equal(12, bars[count - 2].Height);
        Assert.False(bars[count - 1].Second);
    }

    // ---- the hot path -------------------------------------------------------------------------------------------------

    [Fact]
    public void AFrame_AndTheSamplesOnTheirWay_AllocateNothing()
    {
        var history = Filled(History);
        var meter = Filled(Meter);
        var bars = new LevelBar[Math.Max(History.MaxRects, Meter.MaxRects)];
        var historyFeed = new LevelBarsFeed();
        var meterFeed = new LevelBarsFeed();
        long historyCursor = 0;
        long meterCursor = 0;
        var t = 0.0;
        var step = 0;

        // A second of samples at 4 Hz and 20 Hz, drawn at 144 frames per second: the UI thread writes each sample to the feed, the
        // render thread drains it (what LevelBarsHandler.OnMessage does), then lays out the frame.
        long Pass()
        {
            var sum = 0L;
            for (var frame = 0; frame < 144; frame++)
            {
                t += 1.0 / 144;
                step++;
                if (step % 36 == 0)
                {
                    historyFeed.Write((step % 7) / 6f, animate: true);
                }

                if (step % 7 == 0)
                {
                    meterFeed.Write((step % 5) / 4f, animate: true);
                }

                while (historyFeed.TryRead(ref historyCursor, out var value, out var animate))
                {
                    history.Push(value, t, animate);
                }

                while (meterFeed.TryRead(ref meterCursor, out var value, out var animate))
                {
                    meter.Push(value, t, animate);
                }

                sum += history.Layout(bars, 360, 36, history.Position(t), idle: false);
                sum += meter.Layout(bars, 200, 24, meter.Position(t), idle: false);
            }

            return sum;
        }

        Pass();
        var attempts = new List<long>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            Pass();
            attempts.Add(GC.GetAllocatedBytesForCurrentThread() - before);
            if (attempts[^1] == 0)
            {
                break;
            }
        }

        Assert.True(attempts[^1] == 0, $"bytes allocated per 144 frames: {string.Join(", ", attempts)}");
    }

    [Fact]
    public void TheFeed_HandsSamplesOverInOrder_AndCatchesUpAfterALap()
    {
        var feed = new LevelBarsFeed();
        long cursor = 0;
        feed.Write(0.1f, animate: true);
        feed.Write(0.2f, animate: false);

        Assert.True(feed.TryRead(ref cursor, out var a, out var animateA));
        Assert.True(feed.TryRead(ref cursor, out var b, out var animateB));
        Assert.False(feed.TryRead(ref cursor, out _, out _));
        Assert.Equal((0.1f, true, 0.2f, false), (a, animateA, b, animateB));

        for (var i = 0; i < LevelBarsFeed.Capacity + 10; i++)
        {
            feed.Write(i, animate: true);
        }

        Assert.True(feed.TryRead(ref cursor, out var oldest, out _));
        Assert.Equal(10f, oldest);
    }

    // ---- the controls -------------------------------------------------------------------------------------------------

    private static (Window Window, LevelHistoryControl Strip, LevelHistory History) HostHistory(bool reduce = false)
    {
        var history = new LevelHistory();
        for (var i = 0; i < LevelHistory.PastCount; i++)
        {
            history.Push((i % 4) / 3f);
        }

        var strip = new LevelHistoryControl { Width = 360, Height = 36, History = history, PastBrush = Brushes.Black, FutureBrush = Brushes.LightGray };
        var window = new Window { Content = strip, Width = 360, Height = 36, Background = Brushes.White };
        Motion.SetReduceMotion(window, reduce);
        window.Show();
        UiHarness.Pump();
        return (window, strip, history);
    }

    [Fact]
    public async Task TheHistory_SlidesANewSampleIn_AndThenTheRenderLoopRests()
    {
        var (atRest, sliding, moved, settled, sameAsStatic) = await Session.Dispatch(
            () =>
            {
                var (window, strip, history) = HostHistory();
                var handler = strip.Bars.Handler!;
                var a = handler.IsScheduled;
                var rest = UiHarness.Pixels(window);

                // A steady sample a poll interval after the last one: the strip slides.
                Thread.Sleep(250);
                history.Push(1f);
                UiHarness.Pump(1);
                var b = handler.IsScheduled;
                var c = !UiHarness.Pixels(window).AsSpan().SequenceEqual(rest);

                Thread.Sleep(400);
                UiHarness.Pump(3);
                var d = !handler.IsScheduled;
                var after = UiHarness.Pixels(window);

                // The same values drawn without motion: the rest frame is the static strip.
                var (still, _, stillHistory) = HostHistory(reduce: true);
                stillHistory.Push(1f);
                UiHarness.Pump(2);
                var e = UiHarness.Pixels(still).AsSpan().SequenceEqual(after);
                still.Close();
                window.Close();
                return (a, b, c, d, e);
            },
            Ct);

        Assert.False(atRest);
        Assert.True(sliding, "a new sample asks for animation frames");
        Assert.True(moved, "the frame right after the sample is not the rest frame");
        Assert.True(settled, "after one interval the render loop is not woken any more");
        Assert.True(sameAsStatic, "at rest the strip is exactly the static frame");
    }

    [Fact]
    public async Task UnderReducedMotion_OrHidden_ASampleShowsAtOnce_AndNoFrameIsAskedFor()
    {
        var (reduced, hidden, values) = await Session.Dispatch(
            () =>
            {
                var (window, strip, history) = HostHistory(reduce: true);
                Thread.Sleep(250);
                history.Push(1f);
                UiHarness.Pump(1);
                var a = strip.Bars.Handler!.IsScheduled;
                Motion.SetReduceMotion(window, false);

                var panel = new Border();
                window.Content = panel;
                panel.Child = strip;
                UiHarness.Pump(2);
                panel.IsVisible = false;
                Thread.Sleep(250);
                history.Push(0.5f);
                UiHarness.Pump(1);
                var b = strip.Bars.Handler?.IsScheduled ?? false;
                var c = strip.Bars.Handler!.Motion.Values[^1];
                window.Close();
                return (a, b, c);
            },
            Ct);

        Assert.False(reduced);
        Assert.False(hidden);
        Assert.Equal(LevelHistory.Normalize(0.5f), values);
    }

    [Fact]
    public async Task AClearedHistory_IsShownAtOnce_AndSilenceWakesNothing()
    {
        var (cleared, silent) = await Session.Dispatch(
            () =>
            {
                var (window, strip, history) = HostHistory();
                history.Clear();
                UiHarness.Pump(2);
                var a = (strip.Bars.Handler!.IsScheduled, strip.Bars.Handler.Motion.Values[^1]);

                // A flat strip that stays flat: nothing to slide.
                Thread.Sleep(250);
                history.Push(0f);
                UiHarness.Pump(1);
                var b = strip.Bars.Handler.IsScheduled;
                window.Close();
                return (a, b);
            },
            Ct);

        Assert.Equal((false, 0f), cleared);
        Assert.False(silent);
    }

    [Fact]
    public async Task TheMeter_HandsEverySampleToTheRenderThread_AndStaysQuietInSilence()
    {
        var (sent, quiet) = await Session.Dispatch(
            () =>
            {
                var meter = new LevelMeterControl { Width = 200, Height = 24, LevelSource = new FixedLevel(0f), IsActive = true };
                var window = new Window { Content = meter, Width = 300, Height = 100 };
                window.Show();
                UiHarness.Pump();
                var before = meter.Bars.SamplesSent;
                meter.Tick();
                meter.Tick();
                var silence = meter.Bars.SamplesSent - before;
                ((FixedLevel)meter.LevelSource!).Level = 0.6f;
                meter.Tick();
                var r = (meter.Bars.SamplesSent - before, silence);
                window.Close();
                return r;
            },
            Ct);

        Assert.Equal(1, sent);
        Assert.Equal(0, quiet);
    }

    [Fact]
    public async Task Detached_TheBarsVisualIsGone_AndComesBackWithTheValues()
    {
        var (detached, reattached, newest) = await Session.Dispatch(
            () =>
            {
                var (window, strip, history) = HostHistory();
                window.Content = null;
                UiHarness.Pump(1);
                var a = strip.Bars.HasVisual;
                history.Push(1f);
                window.Content = strip;
                UiHarness.Pump(2);
                var r = (a, strip.Bars.HasVisual, strip.Bars.Handler!.Motion.Values[^1]);
                window.Close();
                return r;
            },
            Ct);

        Assert.False(detached);
        Assert.True(reattached);
        Assert.Equal(1f, newest);
    }

    private sealed class FixedLevel(float level) : ILevelSource
    {
        public float Level { get; set; } = level;
    }
}
