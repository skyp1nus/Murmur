using Murmur.Capture.Windows.Screen;

namespace Murmur.Capture.Windows.Tests;

public sealed class FrameTimingTests
{
    private const long Second = MediaTime.TicksPerSecond;

    [Theory]
    [InlineData(10_000_000L, 10_000_000L, 10_000_000L)]   // 1 s at a 10 MHz QPC
    [InlineData(3_579_545L, 3_579_545L, 10_000_000L)]     // 1 s at the ACPI PM timer rate
    [InlineData(1_500L, 1_000L, 15_000_000L)]             // 1.5 s at 1 kHz
    [InlineData(0L, 24_000_000L, 0L)]
    public void FromQpc_ConvertsTo100ns(long counter, long frequency, long expected) =>
        Assert.Equal(expected, MediaTime.FromQpc(counter, frequency));

    [Fact]
    public void FromQpc_DoesNotOverflowForLongUptimes()
    {
        // 300 days at 24 MHz: counter * 10^7 would overflow a long.
        const long frequency = 24_000_000;
        var counter = 300L * 24 * 3600 * frequency;

        Assert.Equal(300L * 24 * 3600 * Second, MediaTime.FromQpc(counter, frequency));
    }

    [Theory]
    [InlineData(30, 333_333)]
    [InlineData(60, 166_667)]
    [InlineData(24, 416_667)]
    [InlineData(1, 10_000_000)]
    public void FrameDuration_RoundsToNearest100ns(int fps, long expected) =>
        Assert.Equal(expected, MediaTime.FrameDuration(fps));

    [Theory]
    [InlineData(60, 30, 30)]
    [InlineData(144, 30, 30)]
    [InlineData(120, 30, 30)]
    [InlineData(75, 30, 30)]
    [InlineData(30, 30, 30)]
    [InlineData(20, 30, 20)]  // slower than the target: every frame passes
    [InlineData(60, 15, 15)]
    [InlineData(60, 60, 60)]
    public void Limiter_YieldsTargetRateFromFasterSources(int sourceHz, int targetFps, int expectedPerSecond)
    {
        var limiter = new FrameRateLimiter(targetFps);
        var accepted = 0;
        var frames = sourceHz * 10;
        for (var k = 0; k < frames; k++)
        {
            if (limiter.TryAccept(5 * Second + (k * Second / sourceHz)))
            {
                accepted++;
            }
        }

        Assert.InRange(accepted, (expectedPerSecond * 10) - 1, (expectedPerSecond * 10) + 1);
    }

    [Fact]
    public void Limiter_ToleratesJitterWithoutAliasing()
    {
        // 60 Hz with +-3 ms of jitter must still give ~30 fps, not 20.
        var limiter = new FrameRateLimiter(30);
        var random = new Random(42);
        var accepted = 0;
        for (var k = 0; k < 600; k++)
        {
            var jitter = random.Next(-30_000, 30_001);
            if (limiter.TryAccept(Second + (k * Second / 60) + jitter))
            {
                accepted++;
            }
        }

        Assert.InRange(accepted, 290, 310);
    }

    [Fact]
    public void Limiter_ReanchorsAfterAGap_InsteadOfBursting()
    {
        var limiter = new FrameRateLimiter(30);
        Assert.True(limiter.TryAccept(0));
        Assert.True(limiter.TryAccept(Second / 30));

        // Static screen for 5 s, then 60 Hz again: no catch-up burst, the grid restarts at the first new frame.
        var start = 5 * Second;
        Assert.True(limiter.TryAccept(start));
        Assert.False(limiter.TryAccept(start + (Second / 60)));
        Assert.True(limiter.TryAccept(start + (2 * Second / 60)));
        Assert.False(limiter.TryAccept(start + (3 * Second / 60)));
    }

    [Fact]
    public void Limiter_RejectsTimestampsGoingBackwards_AndResets()
    {
        var limiter = new FrameRateLimiter(30);
        Assert.True(limiter.TryAccept(10 * Second));
        Assert.False(limiter.TryAccept(9 * Second));

        limiter.Reset();
        Assert.True(limiter.TryAccept(9 * Second));
    }

    [Fact]
    public void Limiter_RejectsInvalidRate() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRateLimiter(0));

    [Fact]
    public void Timeline_IsRelativeToStart_NonNegative_AndStrictlyIncreasing()
    {
        var timeline = new SegmentTimeline();
        timeline.Reset(100 * Second);
        Assert.Equal(-1, timeline.LastSampleTime);

        Assert.Equal(0, timeline.ToSampleTime(99 * Second));            // stale frame from before the start
        Assert.Equal(Second / 2, timeline.ToSampleTime(100 * Second + (Second / 2)));
        Assert.Equal((Second / 2) + 1, timeline.ToSampleTime(100 * Second + (Second / 2))); // duplicate timestamp
        Assert.Equal((Second / 2) + 2, timeline.ToSampleTime(100 * Second));              // went backwards
        Assert.Equal(2 * Second, timeline.ToSampleTime(102 * Second));
        Assert.Equal(2 * Second, timeline.LastSampleTime);

        timeline.Reset(200 * Second);
        Assert.Equal(0, timeline.ToSampleTime(200 * Second));
    }

    [Fact]
    public void Repeat_IsWrittenOnlyWhileAcceptingAndBeforeAFault()
    {
        const long last = 10 * Second;
        const long now = 11 * Second;
        Assert.True(RepeatPolicy.ShouldRepeat(true, true, true, now, last, RepeatPolicy.NoFault, Second));

        Assert.False(RepeatPolicy.ShouldRepeat(true, true, false, now, last, RepeatPolicy.NoFault, Second)); // faulted / stopping
        Assert.False(RepeatPolicy.ShouldRepeat(true, true, true, now, last, now, Second));                   // fault recorded
        Assert.False(RepeatPolicy.ShouldRepeat(false, true, true, now, last, RepeatPolicy.NoFault, Second)); // no frame yet
        Assert.False(RepeatPolicy.ShouldRepeat(true, false, true, now, last, RepeatPolicy.NoFault, Second)); // writer failed
        Assert.False(RepeatPolicy.ShouldRepeat(true, true, true, now - 1, last, RepeatPolicy.NoFault, Second)); // too soon
    }

    [Fact]
    public void End_IsTheStop_WhenNotFaulted()
    {
        const long start = 100 * Second;
        const long stop = start + (3600 * Second);

        Assert.Equal(stop, RepeatPolicy.EndTicks(stopRequested: true, stop, RepeatPolicy.NoFault, now: stop + Second));

        // The encoder thread failed (no stop request): its fault time wins, else "now".
        Assert.Equal(stop + Second, RepeatPolicy.EndTicks(stopRequested: false, 0, RepeatPolicy.NoFault, now: stop + Second));
    }

    [Fact]
    public void End_IsTheFault_NotTheStop()
    {
        // Display unplugged 5 min into a 1 h segment: the file must end at 5 min, not at 60 min.
        const long start = 100 * Second;
        const long stop = start + (3600 * Second);
        const long fault = start + (300 * Second);

        Assert.Equal(fault, RepeatPolicy.EndTicks(stopRequested: true, stop, fault, now: stop));
        Assert.Equal(fault, RepeatPolicy.EndTicks(stopRequested: false, 0, fault, now: stop));

        // The last frame held at 290 s lasts until the fault.
        var timing = new HeldFrameTiming(MediaTime.TicksPerSecond);
        timing.Advance(0, out _, out _);
        timing.Advance(290 * Second, out _, out _);
        Assert.True(timing.Close(fault - start, out var time, out var duration));
        Assert.Equal((290 * Second, 10 * Second), (time, duration));
    }

    [Fact]
    public void HeldTiming_StaticScreen_DurationsAddUpToWallClock()
    {
        // 600 s mostly static: 30 frames at 30 fps, then a repeat once a second, stop at 600 s. Before the fix every
        // sample lasted 1/30 s and the file collapsed to about 23 s.
        var frame = MediaTime.FrameDuration(30);
        var timeline = new SegmentTimeline();
        var timing = new HeldFrameTiming(MediaTime.TicksPerSecond);
        const long start = 50 * Second;
        timeline.Reset(start);
        long total = 0, written = 0, expectedNext = 0;

        void Take(long ticks)
        {
            if (timing.Advance(timeline.ToSampleTime(ticks), out var time, out var duration))
            {
                Assert.Equal(expectedNext, time); // contiguous: no gaps, no overlaps
                Assert.True(duration >= 1);
                expectedNext = time + duration;
                total += duration;
                written++;
            }
        }

        for (var i = 0; i < 30; i++)
        {
            Take(start + (Second / 20) + (i * frame)); // the first frame arrives 50 ms after the start
        }

        for (var s = 2; s < 600; s++)
        {
            Take(start + (s * Second));
        }

        Assert.True(timing.Close(600 * Second, out var lastTime, out var lastDuration));
        Assert.Equal(expectedNext, lastTime);
        total += lastDuration;
        written++;

        Assert.Equal(30 + 598, written);
        Assert.Equal(600 * Second, total);
        Assert.False(timing.HasHeld);
    }

    [Fact]
    public void HeldTiming_FirstFrameStartsAtZero_AndDurationsArePositive()
    {
        var timing = new HeldFrameTiming(MediaTime.TicksPerSecond);
        Assert.False(timing.HasHeld);
        Assert.Equal(HeldFrameTiming.None, timing.HeldTime);
        Assert.False(timing.Close(10 * Second, out _, out _));

        Assert.False(timing.Advance(2 * Second, out _, out _)); // first frame: nothing to write yet
        Assert.Equal(0, timing.HeldTime);
        Assert.True(timing.Advance(3 * Second, out var time, out var duration));
        Assert.Equal((0L, 3 * Second), (time, duration));

        // The end before the held frame (a fault raced the last frame): still a positive duration.
        Assert.True(timing.Close(Second, out time, out duration));
        Assert.Equal((3 * Second, 1L), (time, duration));
        Assert.False(timing.HasHeld);

        // Discard forgets the held frame; the next one starts at 0 again.
        timing.Advance(5 * Second, out _, out _);
        timing.Discard();
        Assert.False(timing.HasHeld);
        Assert.False(timing.Advance(7 * Second, out _, out _));
        Assert.Equal(0, timing.HeldTime);
    }

    [Theory]
    [InlineData(144, 30)] // vsync gaps of 4 and 5 refreshes: 833.33 and 1041.67 units, a fixed 1:4 pattern
    [InlineData(144, 60)] // 2 and 3 refreshes
    [InlineData(165, 30)]
    [InlineData(120, 30)]
    [InlineData(60, 30)]
    [InlineData(75, 60)]
    public void HeldTiming_OneHourOfVsyncPacedFrames_SinkRoundedTotalMatchesWallClock(int refreshHz, int fps)
    {
        // The sink rounds each duration on its own to its timescale. Raw tick gaps repeat in a fixed vsync pattern, so
        // per-sample rounding errors pile up one way (about 0.7 s/h at 144 Hz); the quantized timeline must not drift.
        var timescale = HeldFrameTiming.SinkTimescale(fps);
        var limiter = new FrameRateLimiter(fps);
        var timeline = new SegmentTimeline();
        var timing = new HeldFrameTiming(timescale);
        const long start = 1234 * Second;
        const long length = 3600 * Second;
        timeline.Reset(start);
        long roundedUnits = 0, truncatedUnits = 0, naiveUnits = 0, samples = 0, previous = -1;

        void Count(long duration)
        {
            roundedUnits += SinkRound(duration, timescale);
            truncatedUnits += duration * timescale / Second;
            samples++;
        }

        for (long k = 0; ; k++)
        {
            var ticks = start + (Second / 50) + (k * Second / refreshHz);
            if (ticks - start >= length)
            {
                break;
            }

            if (!limiter.TryAccept(ticks))
            {
                continue;
            }

            var time = timeline.ToSampleTime(ticks);
            if (previous >= 0)
            {
                naiveUnits += SinkRound(time - previous, timescale); // the raw gap, as the first fix wrote it
            }

            previous = time;
            if (timing.Advance(time, out _, out var duration))
            {
                Count(duration);
            }
        }

        Assert.True(timing.Close(length, out _, out var last));
        Count(last);

        var wallUnits = length * timescale / Second;
        Assert.InRange(samples, ((long)fps * 3600) - fps, ((long)fps * 3600) + fps);
        Assert.InRange(roundedUnits, wallUnits - 1, wallUnits + 1);
        Assert.Equal(roundedUnits, truncatedUnits); // exact whether the sink rounds or truncates

        // The raw gaps alone do drift on high-refresh displays (this is what the test guards against).
        if (refreshHz == 144)
        {
            naiveUnits += SinkRound(length - previous, timescale);
            Assert.True(Math.Abs(naiveUnits - wallUnits) > timescale / 4, $"raw gaps drifted only {naiveUnits - wallUnits} units");
        }
    }

    [Fact]
    public void HeldTiming_Quantized_StartsAndEndsOnWholeUnits_AndLastsAtLeastOneUnit()
    {
        var timescale = HeldFrameTiming.SinkTimescale(30);
        Assert.Equal(30_000, timescale);
        var timing = new HeldFrameTiming(timescale);
        Assert.Equal(timescale, timing.Timescale);

        Assert.False(timing.Advance(0, out _, out _));
        Assert.True(timing.Advance(277_778, out var time, out var duration)); // 833.33 units -> 833
        Assert.Equal(0, time);
        Assert.Equal(833, SinkRound(duration, timescale));
        Assert.Equal(timing.UnitsToTicks(833), timing.HeldTime);

        // A repeat racing a captured frame 100 ns later: the repeat still lasts one unit, never zero.
        Assert.True(timing.Advance(277_779, out time, out duration));
        Assert.Equal(833, SinkRound(time, timescale));
        Assert.Equal(1, SinkRound(duration, timescale));

        // The next frame starts a unit late, and the timeline catches up at the following one.
        Assert.True(timing.Advance(10 * Second, out time, out duration));
        Assert.Equal(834, SinkRound(time, timescale));
        Assert.Equal(300_000 - 834, SinkRound(duration, timescale));

        // The end before the held frame (a fault raced the last frame): one unit.
        Assert.True(timing.Close(Second, out time, out duration));
        Assert.Equal(300_000, SinkRound(time, timescale));
        Assert.Equal(1, SinkRound(duration, timescale));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15_000)]
    [InlineData(30_000)]
    [InlineData(60_000)]
    [InlineData(144_000)]
    [InlineData(240_000)]
    [InlineData(10_000_000)]
    public void HeldTiming_UnitsToTicks_ConvertsBackToTheSameUnits(long timescale)
    {
        var timing = new HeldFrameTiming(timescale);
        foreach (var units in (long[])[0, 1, 2, 3, 7, 833, 1042, 999_999, 108_000_000, 2_592_000_000])
        {
            var ticks = timing.UnitsToTicks(units);
            Assert.Equal(units, SinkRound(ticks, timescale));
            Assert.Equal(units, ticks * timescale / Second); // truncating sinks agree
            Assert.Equal(units, timing.TicksToUnits(ticks));
        }

        Assert.Equal(0, timing.TicksToUnits(-5));
    }

    [Fact]
    public void HeldTiming_RejectsInvalidTimescales()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HeldFrameTiming(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HeldFrameTiming(Second + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => HeldFrameTiming.SinkTimescale(0));
        Assert.Equal(60_000, HeldFrameTiming.SinkTimescale(60));
    }

    /// <summary>How the FMPEG4 sink stores a duration: 100 ns ticks rounded to the nearest unit of its timescale.</summary>
    private static long SinkRound(long ticks, long timescale) => ((ticks * timescale) + (Second / 2)) / Second;

    [Fact]
    public void FramePath_Helpers_DoNotAllocate()
    {
        var limiter = new FrameRateLimiter(30);
        var timeline = new SegmentTimeline();
        var ring = new SpscRing<(nint, long)>(4);
        var timing = new HeldFrameTiming(HeldFrameTiming.SinkTimescale(30));
        timeline.Reset(0);
        long sum = 0;

        void Run(int from)
        {
            for (var k = from; k < from + 600; k++)
            {
                var ticks = k * Second / 60;
                if (limiter.TryAccept(ticks) && ring.TryEnqueue((k, ticks)) && ring.TryDequeue(out var item))
                {
                    timing.Advance(timeline.ToSampleTime(item.Item2), out var time, out var duration);
                    sum += time + duration;
                }
            }
        }

        Run(0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Run(600);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(sum > 0);
    }
}
