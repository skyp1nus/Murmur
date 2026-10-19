using Murmur.Capture.Windows.Screen;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// The segment clock of <see cref="WgcScreenRecorder"/> in pure timeline math (<see cref="SegmentTimeline"/> and
/// <see cref="HeldFrameTiming"/>; dropping what arrives after the stop call is <see cref="RepeatPolicy.IsAfterEnd"/>,
/// tested below, and the encoder thread's use of it by the Windows-trait tests): media time 0 is the moment
/// StartSegmentAsync is called and the file ends when StopSegmentAsync is called, however long start-up and stopping
/// take (the contract of IScreenRecorder). Runs everywhere; the real recorder is covered by the Windows-trait tests.
/// </summary>
public sealed class SegmentClockTests
{
    private const long Second = MediaTime.TicksPerSecond;
    private const long Ms = Second / 1000;
    private const int FrameRate = 60;

    /// <summary>
    /// Replays the encoder thread's timeline calls for frames captured before the stop call and returns the sum of the
    /// written durations (100 ns).
    /// </summary>
    private static long WrittenDuration(long startTicks, long stopTicks, params long[] frameTicks)
    {
        var timeline = new SegmentTimeline();
        timeline.Reset(startTicks);
        var timing = new HeldFrameTiming(HeldFrameTiming.SinkTimescale(FrameRate));
        long total = 0;
        foreach (var ticks in frameTicks)
        {
            Assert.False(RepeatPolicy.IsAfterEnd(ticks, stopTicks), "the encoder thread drops frames captured after the stop call");
            if (timing.Advance(timeline.ToSampleTime(ticks), out _, out var duration))
            {
                total += duration;
            }
        }

        if (timing.Close(stopTicks - timeline.Start, out _, out var last))
        {
            total += last;
        }

        return total;
    }

    private static void AssertWithinOneUnit(long expected, long actual) =>
        Assert.InRange(actual, expected, expected + (2 * Second / (FrameRate * 1000L)));

    [Fact]
    public void SlowStartUp_IsCoveredByTheFirstFrame()
    {
        // StartSegmentAsync is called at t = 100 s; the encoder and capture session take 450 ms, so the first frame
        // arrives at 100.45 s; the segment is stopped at 110 s.
        const long start = 100 * Second;
        var duration = WrittenDuration(start, 110 * Second, start + (450 * Ms), start + (467 * Ms), start + (5 * Second));

        AssertWithinOneUnit(10 * Second, duration);
    }

    [Fact]
    public void TheOldStartTime_ShortenedTheFileByTheStartUp()
    {
        // Documents the bug: with media time 0 taken after the set-up (first frame time) the file came out 450 ms short.
        const long call = 100 * Second;
        const long afterSetUp = call + (450 * Ms);

        var duration = WrittenDuration(afterSetUp, 110 * Second, afterSetUp, afterSetUp + (17 * Ms));

        AssertWithinOneUnit((10 * Second) - (450 * Ms), duration);
    }

    [Fact]
    public void FirstFrame_StartsAtMediaTimeZero_EvenWhenItArrivesLate()
    {
        var timeline = new SegmentTimeline();
        timeline.Reset(50 * Second);
        var timing = new HeldFrameTiming(HeldFrameTiming.SinkTimescale(FrameRate));

        Assert.False(timing.Advance(timeline.ToSampleTime(50 * Second + (450 * Ms)), out _, out _));
        Assert.Equal(0, timing.HeldTime);
        Assert.True(timing.Advance(timeline.ToSampleTime(50 * Second + (467 * Ms)), out var time, out var duration));
        Assert.Equal(0, time);
        AssertWithinOneUnit(467 * Ms, duration); // the first frame lasts from the call to the second frame
    }

    [Fact]
    public void TheFileEndsAtTheStopCall_NotWhenTheEncoderThreadFinishes()
    {
        // The stop call comes at 110 s and the encoder thread closes the timeline at 110.7 s: the last frame lasts
        // until the stop call time (RepeatPolicy.EndTicks), not until the thread got round to it.
        const long start = 100 * Second;
        const long stop = 110 * Second;
        var end = RepeatPolicy.EndTicks(true, stop, RepeatPolicy.NoFault, stop + (700 * Ms));
        var duration = WrittenDuration(start, end, start + (20 * Ms), stop - (100 * Ms));

        AssertWithinOneUnit(10 * Second, duration);
    }

    [Fact]
    public void NoFrameAtAll_PlaceholderStillLastsFromTheCallToTheStop()
    {
        const long start = 100 * Second;
        var timeline = new SegmentTimeline();
        timeline.Reset(start);
        var timing = new HeldFrameTiming(HeldFrameTiming.SinkTimescale(FrameRate));

        Assert.False(timing.Advance(timeline.ToSampleTime(PlaceholderFrame.CaptureTicks(timeline)), out _, out _));
        Assert.True(timing.Close(110 * Second - timeline.Start, out var time, out var duration));

        Assert.Equal(0, time);
        AssertWithinOneUnit(10 * Second, duration);
    }

    [Fact]
    public void IsAfterEnd_IsStrictAndNeverTrueWhileRunning()
    {
        Assert.False(RepeatPolicy.IsAfterEnd(10 * Second, 10 * Second));
        Assert.True(RepeatPolicy.IsAfterEnd((10 * Second) + 1, 10 * Second));
        Assert.False(RepeatPolicy.IsAfterEnd(long.MaxValue - 1, RepeatPolicy.NoStop));
    }

    [Fact]
    public void StopRequest_TheEarliestRequestWins()
    {
        long cell = RepeatPolicy.NoStop;

        Assert.True(StopRequest.Publish(ref cell, 10 * Second));
        Assert.False(StopRequest.Publish(ref cell, 11 * Second));
        Assert.Equal(10 * Second, cell);
    }

    [Fact]
    public void StopRequest_ACancelledRequestIsRetracted_ButNeverSomeoneElsesStop()
    {
        long cell = RepeatPolicy.NoStop;
        Assert.True(StopRequest.Publish(ref cell, 10 * Second));

        Assert.True(StopRequest.Retract(ref cell, 10 * Second));
        Assert.Equal(RepeatPolicy.NoStop, cell);
        Assert.False(RepeatPolicy.IsAfterEnd(50 * Second, cell)); // the segment records on

        // A later request lost the race to an earlier stop: retracting it must leave the earlier stop alone.
        Assert.True(StopRequest.Publish(ref cell, 20 * Second));
        Assert.False(StopRequest.Publish(ref cell, 21 * Second));
        Assert.False(StopRequest.Retract(ref cell, 21 * Second));
        Assert.Equal(20 * Second, cell);
    }

    [Fact]
    public void End_UsesTheStopCallTime_NotTheTimeTheEncoderThreadFinishes()
    {
        Assert.Equal(10 * Second, RepeatPolicy.EndTicks(true, 10 * Second, RepeatPolicy.NoFault, 11 * Second));
        Assert.Equal(11 * Second, RepeatPolicy.EndTicks(false, RepeatPolicy.NoStop, RepeatPolicy.NoFault, 11 * Second));
    }
}
