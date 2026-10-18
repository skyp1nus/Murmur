using Murmur.Capture.Windows.Screen;

namespace Murmur.Capture.Windows.Tests;

public sealed class RepeatPolicyEdgeTests
{
    private const long Second = MediaTime.TicksPerSecond;

    [Fact]
    public void ShouldRepeat_FiresExactlyAtTheInterval()
    {
        Assert.False(RepeatPolicy.ShouldRepeat(true, true, true, (2 * Second) - 1, Second, RepeatPolicy.NoFault, Second));
        Assert.True(RepeatPolicy.ShouldRepeat(true, true, true, 2 * Second, Second, RepeatPolicy.NoFault, Second));
    }

    [Fact]
    public void ShouldRepeat_StopsOnceNowReachesTheFault()
    {
        Assert.True(RepeatPolicy.ShouldRepeat(true, true, true, (5 * Second) - 1, Second, 5 * Second, Second));
        Assert.False(RepeatPolicy.ShouldRepeat(true, true, true, 5 * Second, Second, 5 * Second, Second));
        Assert.False(RepeatPolicy.ShouldRepeat(true, true, true, 9 * Second, Second, 5 * Second, Second));
    }

    [Fact]
    public void End_UsesTheEarlierOfStopAndFault()
    {
        Assert.Equal(10 * Second, RepeatPolicy.EndTicks(true, 10 * Second, 20 * Second, 30 * Second)); // fault after stop: stop wins
        Assert.Equal(10 * Second, RepeatPolicy.EndTicks(true, 20 * Second, 10 * Second, 30 * Second));
    }

    [Fact]
    public void Close_WhenTheFaultPrecedesTheLastSample_StillGivesAPositiveDuration()
    {
        var timing = new HeldFrameTiming();
        timing.Advance(0, out _, out _);
        timing.Advance(10 * Second, out _, out _);
        Assert.True(timing.Close(RepeatPolicy.EndTicks(true, 100 * Second, 5 * Second, 100 * Second), out var time, out var duration));
        Assert.Equal(10 * Second, time);
        Assert.Equal(1, duration);
    }

    [Fact]
    public void Advance_WithNonIncreasingTimes_KeepsTheTimelineContiguous()
    {
        // SegmentTimeline already makes times strictly increasing; the timing still never yields a gap or an overlap.
        var timing = new HeldFrameTiming();
        timing.Advance(5 * Second, out _, out _);
        Assert.True(timing.Advance(0, out var time, out var duration));
        Assert.Equal((0L, 1L), (time, duration));
        Assert.Equal(1, timing.HeldTime);
        Assert.True(timing.Advance(Second, out time, out duration));
        Assert.Equal((1L, Second - 1), (time, duration));
    }

    [Fact]
    public void Advance_AtThirtyFps_WritesOneFrameDurationPerFrame()
    {
        var frame = MediaTime.FrameDuration(30);
        var timing = new HeldFrameTiming();
        timing.Advance(0, out _, out _);
        for (var i = 1; i <= 90; i++)
        {
            Assert.True(timing.Advance(i * frame, out var time, out var duration));
            Assert.Equal((i - 1) * frame, time);
            Assert.Equal(frame, duration);
        }
    }

    [Fact]
    public void Timing_DoesNotAllocate()
    {
        var timing = new HeldFrameTiming();
        long sum = 0;

        void Run()
        {
            for (var i = 0; i < 1000; i++)
            {
                if (timing.Advance(i * Second, out _, out var duration))
                {
                    sum += duration;
                }
            }

            timing.Close(1000 * Second, out _, out var last);
            sum += last;
        }

        Run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(2000 * Second, sum);
    }
}
