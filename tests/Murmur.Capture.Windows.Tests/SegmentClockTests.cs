using Murmur.Capture.Windows.Screen;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// The segment clock of <see cref="WgcScreenRecorder"/> in pure timeline math: media time 0 is the moment
/// StartSegmentAsync is called and the file ends when StopSegmentAsync is called, however long start-up and stopping
/// take (the contract of IScreenRecorder). Runs everywhere; the real recorder is covered by the Windows-trait tests.
/// </summary>
public sealed class SegmentClockTests
{
    private const long Second = MediaTime.TicksPerSecond;
    private const long Ms = Second / 1000;
    private const int FrameRate = 60;

    /// <summary>Replays the encoder thread's timeline calls and returns the sum of the written durations (100 ns).</summary>
    private static long WrittenDuration(long startTicks, long stopTicks, params long[] frameTicks)
    {
        var timeline = new SegmentTimeline();
        timeline.Reset(startTicks);
        var timing = new HeldFrameTiming(HeldFrameTiming.SinkTimescale(FrameRate));
        long total = 0;
        foreach (var ticks in frameTicks)
        {
            if (RepeatPolicy.IsAfterEnd(ticks, stopTicks))
            {
                continue; // the encoder thread drops frames captured after the stop call
            }

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
    public void SlowStop_IsNotPartOfTheFile()
    {
        // The stop call comes at 110 s, finalizing takes 700 ms; the encoder thread would wake up at 110.7 s but the
        // segment ends at the call.
        const long start = 100 * Second;
        const long stop = 110 * Second;
        var duration = WrittenDuration(start, stop, start + (20 * Ms), stop + (300 * Ms), stop + (690 * Ms));

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
    public void End_UsesTheStopCallTime_NotTheTimeTheEncoderThreadFinishes()
    {
        Assert.Equal(10 * Second, RepeatPolicy.EndTicks(true, 10 * Second, RepeatPolicy.NoFault, 11 * Second));
        Assert.Equal(11 * Second, RepeatPolicy.EndTicks(false, RepeatPolicy.NoStop, RepeatPolicy.NoFault, 11 * Second));
    }
}
