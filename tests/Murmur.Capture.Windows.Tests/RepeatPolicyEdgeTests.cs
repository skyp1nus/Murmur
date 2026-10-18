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
    public void Tail_UsesTheEarlierOfStopAndFault()
    {
        var frame = MediaTime.FrameDuration(30);
        const long start = 0;

        // Fault after stop: stop wins.
        Assert.True(RepeatPolicy.TryGetTail(true, true, 10 * Second, 20 * Second, frame, start, Second, out var tail));
        Assert.Equal((10 * Second) - frame, tail);
    }

    [Fact]
    public void Tail_IsSkippedWhenFaultPrecedesTheLastSample()
    {
        var frame = MediaTime.FrameDuration(30);
        Assert.False(RepeatPolicy.TryGetTail(true, true, 100 * Second, 10 * Second, frame, 0, 10 * Second, out _));
    }

    [Fact]
    public void Tail_RequiresMoreThanHalfAFrameOfGap()
    {
        var frame = MediaTime.FrameDuration(30);
        const long stop = 10 * Second;
        var tailTime = stop - frame;
        // last sample exactly half a frame before the tail: not enough.
        Assert.False(RepeatPolicy.TryGetTail(true, true, stop, RepeatPolicy.NoFault, frame, 0, tailTime - (frame / 2), out _));
        Assert.True(RepeatPolicy.TryGetTail(true, true, stop, RepeatPolicy.NoFault, frame, 0, tailTime - (frame / 2) - 1, out _));
    }

    [Fact]
    public void Tail_WithFaultAtSegmentStart_IsNotProduced()
    {
        var frame = MediaTime.FrameDuration(30);
        Assert.False(RepeatPolicy.TryGetTail(true, true, 100 * Second, 50 * Second, frame, 50 * Second, 0, out _));
    }
}
