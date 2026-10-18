using Murmur.Capture.Windows.Screen;

namespace Murmur.Capture.Windows.Tests;

/// <summary>The black placeholder frame of segments in which no frame reached the encoder (pure logic, any OS).</summary>
public sealed class PlaceholderFrameTests
{
    private const long Second = MediaTime.TicksPerSecond;

    [Fact]
    public void ShouldWrite_OnlyWhenNothingWasWritten_AndTheWriterIsHealthy()
    {
        Assert.True(PlaceholderFrame.ShouldWrite(anySampleWritten: false, writerHealthy: true));   // WGC delivered nothing
        Assert.False(PlaceholderFrame.ShouldWrite(anySampleWritten: true, writerHealthy: true));   // real frames: never
        Assert.False(PlaceholderFrame.ShouldWrite(anySampleWritten: false, writerHealthy: false)); // writer failed: it would fail too
        Assert.False(PlaceholderFrame.ShouldWrite(anySampleWritten: true, writerHealthy: false));
    }

    [Fact]
    public void Placeholder_IsWrittenAtSampleTimeZero()
    {
        var timeline = new SegmentTimeline();
        timeline.Reset(123 * Second);

        Assert.Equal(123 * Second, PlaceholderFrame.CaptureTicks(timeline));
        Assert.Equal(0, timeline.ToSampleTime(PlaceholderFrame.CaptureTicks(timeline)));
        Assert.Equal(0, timeline.LastSampleTime);
    }

    [Fact]
    public void EmptySegment_GetsOnePlaceholder_ThatLastsTheWholeSegment()
    {
        // Mirrors the encoder thread at stop: nothing is held, so the placeholder is held at sample time 0 and the
        // end of the segment fixes its duration: the one-frame file is as long as the segment (audio stays in sync).
        var timeline = new SegmentTimeline();
        var timing = new HeldFrameTiming();
        const long start = 10 * Second;
        timeline.Reset(start);
        var end = RepeatPolicy.EndTicks(stopRequested: true, start + (3 * Second), RepeatPolicy.NoFault, now: start + (4 * Second));

        Assert.False(timing.Close(end - timeline.Start, out _, out _)); // no frame held: nothing to close
        Assert.True(PlaceholderFrame.ShouldWrite(anySampleWritten: false, writerHealthy: true));
        Assert.False(timing.Advance(timeline.ToSampleTime(PlaceholderFrame.CaptureTicks(timeline)), out _, out _));
        Assert.True(timing.Close(end - timeline.Start, out var time, out var duration));
        Assert.Equal(0, time);
        Assert.Equal(3 * Second, duration);
    }

    [Fact]
    public void FaultBeforeTheFirstFrame_StillGetsAPlaceholder_EndingAtTheFault()
    {
        // A display unplugged before WGC delivered anything: no repeat, but the file must still finalize, and its
        // placeholder lasts until the fault, not until the stop.
        const long start = 0;
        const long fault = Second;
        Assert.False(RepeatPolicy.ShouldRepeat(false, true, false, 2 * Second, 0, fault, Second));
        Assert.True(PlaceholderFrame.ShouldWrite(anySampleWritten: false, writerHealthy: true));

        var timing = new HeldFrameTiming();
        timing.Advance(0, out _, out _);
        Assert.True(timing.Close(RepeatPolicy.EndTicks(true, 5 * Second, fault, 6 * Second) - start, out _, out var duration));
        Assert.Equal(Second, duration);
    }

    [Fact]
    public void StaticScreenRepeats_AreUnaffected_OnceRealFramesArrive()
    {
        // The first real frame arrives late (static screen at start): it stands for the picture since the segment
        // start, repeats work as before and no placeholder is written.
        var timeline = new SegmentTimeline();
        var timing = new HeldFrameTiming();
        const long start = 0;
        timeline.Reset(start);

        Assert.False(timing.Advance(timeline.ToSampleTime(4 * Second), out _, out _));
        Assert.Equal(0, timing.HeldTime);
        Assert.True(RepeatPolicy.ShouldRepeat(true, true, true, 5 * Second, 4 * Second, RepeatPolicy.NoFault, Second));
        Assert.True(timing.Advance(timeline.ToSampleTime(5 * Second), out var time, out var duration));
        Assert.Equal((0L, 5 * Second), (time, duration));
        Assert.True(timing.Close((10 * Second) - start, out time, out duration));
        Assert.Equal((5 * Second, 5 * Second), (time, duration));
        Assert.False(PlaceholderFrame.ShouldWrite(anySampleWritten: true, writerHealthy: true));
    }

    [Fact]
    public void Picture_Geometry()
    {
        Assert.Equal(1920u * 4, PlaceholderFrame.RowPitch(1920));
        Assert.Equal(1366L * 768, PlaceholderFrame.PixelCount(new PixelSize(1366, 768)));
        Assert.Equal(7680L * 4320, PlaceholderFrame.PixelCount(new PixelSize(7680, 4320)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaceholderFrame.RowPitch(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaceholderFrame.PixelCount(new PixelSize(0, 768)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaceholderFrame.PixelCount(new PixelSize(1024, -1)));
    }

    [Fact]
    public void OpaqueBlack_IsBgraZeroWithFullAlpha()
    {
        Span<byte> pixel = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pixel, PlaceholderFrame.OpaqueBlackBgra);
        Assert.Equal([0, 0, 0, 255], pixel.ToArray());
    }

    [Fact]
    public void Decision_DoesNotAllocate()
    {
        var timeline = new SegmentTimeline();
        var hits = 0;
        void Run()
        {
            for (var i = 0; i < 1000; i++)
            {
                timeline.Reset(i);
                if (PlaceholderFrame.ShouldWrite((i & 1) == 0, (i & 2) == 0) && timeline.ToSampleTime(PlaceholderFrame.CaptureTicks(timeline)) == 0)
                {
                    hits++;
                }
            }
        }

        Run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(2 * 250, hits); // two runs; i % 4 == 1: nothing written, writer healthy
    }
}
