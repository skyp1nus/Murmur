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
    public void EmptySegment_GetsNoTail_ButOnePlaceholder()
    {
        // Mirrors the encoder thread at stop: the tail is decided first (there is no last frame, so none),
        // then the placeholder is written so Finalize has one sample; the file is exactly one frame long.
        var frame = MediaTime.FrameDuration(30);
        var timeline = new SegmentTimeline();
        const long start = 10 * Second;
        timeline.Reset(start);

        var tail = RepeatPolicy.TryGetTail(false, true, start + (3 * Second), RepeatPolicy.NoFault, frame, timeline.Start, timeline.LastSampleTime, out _);
        Assert.False(tail);
        Assert.True(PlaceholderFrame.ShouldWrite(anySampleWritten: false, writerHealthy: true));
        Assert.Equal(0, timeline.ToSampleTime(PlaceholderFrame.CaptureTicks(timeline)));
    }

    [Fact]
    public void FaultBeforeTheFirstFrame_StillGetsAPlaceholder()
    {
        // A display unplugged before WGC delivered anything: no repeat, no tail, but the file must still finalize.
        var frame = MediaTime.FrameDuration(30);
        const long start = 0;
        const long fault = Second;
        Assert.False(RepeatPolicy.ShouldRepeat(false, true, false, 2 * Second, 0, fault, Second));
        Assert.False(RepeatPolicy.TryGetTail(false, true, 5 * Second, fault, frame, start, -1, out _));
        Assert.True(PlaceholderFrame.ShouldWrite(anySampleWritten: false, writerHealthy: true));
    }

    [Fact]
    public void StaticScreenRepeats_AreUnaffected_OnceRealFramesArrive()
    {
        // The first real frame arrives late (static screen at start): repeats and the tail work as before and no
        // placeholder is written.
        var frame = MediaTime.FrameDuration(30);
        var timeline = new SegmentTimeline();
        const long start = 0;
        timeline.Reset(start);

        var first = timeline.ToSampleTime(4 * Second);
        Assert.Equal(4 * Second, first);
        Assert.True(RepeatPolicy.ShouldRepeat(true, true, true, 5 * Second, 4 * Second, RepeatPolicy.NoFault, Second));
        Assert.True(RepeatPolicy.TryGetTail(true, true, 10 * Second, RepeatPolicy.NoFault, frame, start, timeline.LastSampleTime, out var tail));
        Assert.Equal((10 * Second) - frame, tail);
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
