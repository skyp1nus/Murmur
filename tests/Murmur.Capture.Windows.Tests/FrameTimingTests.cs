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
        var timing = new HeldFrameTiming();
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
        var timing = new HeldFrameTiming();
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
        var timing = new HeldFrameTiming();
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

    [Fact]
    public void FramePath_Helpers_DoNotAllocate()
    {
        var limiter = new FrameRateLimiter(30);
        var timeline = new SegmentTimeline();
        var ring = new SpscRing<(nint, long)>(4);
        timeline.Reset(0);
        long sum = 0;

        void Run(int from)
        {
            for (var k = from; k < from + 600; k++)
            {
                var ticks = k * Second / 60;
                if (limiter.TryAccept(ticks) && ring.TryEnqueue((k, ticks)) && ring.TryDequeue(out var item))
                {
                    sum += timeline.ToSampleTime(item.Item2);
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
