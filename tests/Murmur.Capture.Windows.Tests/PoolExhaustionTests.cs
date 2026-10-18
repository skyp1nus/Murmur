using Murmur.Capture.Windows.Screen;

namespace Murmur.Capture.Windows.Tests;

public sealed class PoolExhaustionTests
{
    private static readonly long Second = MediaTime.TicksPerSecond;

    [Theory]
    [InlineData(0, 0, 12, 12, true)]    // nothing free, cannot grow: AllocateSample would block ~1 s and fail
    [InlineData(0, 0, 13, 12, true)]
    [InlineData(0, 1, 12, 12, false)]   // a sample came back
    [InlineData(0, 0, 11, 12, false)]   // the allocator can still create one
    [InlineData(0, 0, 0, 0, true)]
    [InlineData(-2147467259, 0, 12, 12, false)] // GetFreeSampleCount failed (E_FAIL): no evidence, ask the allocator
    public void SamplePoolProbe_IsExhausted_OnlyWhenNothingIsFree_AndThePoolCannotGrow(int hr, int free, int handedOut, uint maximum, bool expected) =>
        Assert.Equal(expected, SamplePoolProbe.IsExhausted(hr, free, handedOut, maximum));

    [Fact]
    public void Tracker_ReportsOnce_WhenDropsLastTwoSeconds_AndTenFramesInARow()
    {
        var tracker = default(PoolExhaustionTracker);
        var start = 100 * Second;
        var step = Second / 30;
        var reports = 0;
        for (var i = 0; i < 90; i++)
        {
            if (tracker.OnExhausted(start + (i * step)))
            {
                reports++;
                Assert.Equal(62, tracker.Consecutive); // drop 62 is the first one 2 s (61 * 1/30 s) after the first
                Assert.True(tracker.Elapsed(start + (i * step)) >= PoolExhaustionTracker.Duration);
            }
        }

        Assert.Equal(1, reports);
        Assert.Equal(90, tracker.Consecutive);
    }

    [Fact]
    public void Tracker_BurstsOfBackPressure_AreNotReported()
    {
        var tracker = default(PoolExhaustionTracker);
        var now = 0L;
        for (var burst = 0; burst < 20; burst++)
        {
            for (var i = 0; i < 30; i++, now += Second / 30)
            {
                Assert.False(tracker.OnExhausted(now)); // 1 s of drops ...
            }

            tracker.OnAllocated(); // ... then a sample comes back
            Assert.Equal(0, tracker.Consecutive);
            Assert.Equal(0, tracker.Elapsed(now));
        }
    }

    [Fact]
    public void Tracker_FewDropsFarApart_AreNotReported()
    {
        // A static screen delivers a frame every few seconds: two drops 5 s apart prove nothing.
        var tracker = default(PoolExhaustionTracker);
        for (var i = 0; i < PoolExhaustionTracker.MinimumDrops - 1; i++)
        {
            Assert.False(tracker.OnExhausted(i * 5 * Second));
        }

        Assert.True(tracker.OnExhausted(60 * Second));
        Assert.False(tracker.OnExhausted(61 * Second)); // reported once per segment
        tracker.OnAllocated();
        for (var i = 0; i < 100; i++)
        {
            Assert.False(tracker.OnExhausted((100 + i) * Second));
        }
    }

    [Fact]
    public void Naming_NamesThePathTheSamplesAndTheDrops_AndTheFallbackOnlyForTheGpuPath()
    {
        Assert.Equal(
            "GPU zero-copy: all 12 encoder samples stayed in use for 2.5 s (75 frames dropped in a row); the encoder does not hand frames back; the next segments use CPU readback",
            PoolExhaustionNaming.Fault(EncoderInput.Gpu, 12, 75, 25_000_000));
        Assert.Equal(
            "CPU readback: all 8 encoder samples stayed in use for 2.0 s (10 frames dropped in a row); the encoder does not hand frames back",
            PoolExhaustionNaming.Fault(EncoderInput.Cpu, 8, 10, 20_000_000));
        Assert.Equal(
            "CPU readback: the GPU zero-copy sample pool (12 samples) ran dry in an earlier segment",
            PoolExhaustionNaming.FallbackReason(EncoderInput.Gpu, 12));
    }
}
