using Murmur.Capture.Windows.Audio;

namespace Murmur.Capture.Windows.Tests;

public sealed class CaptureClockTests
{
    private const int Rate = 48_000;
    private const int Tolerance = 1_920; // 40 ms = 2 x 20 ms buffer
    private const int Packet = 480;      // 10 ms
    private const long Start = 123_456_789_000;

    private static long T(long frames) => Ticks.At(frames, Rate, Start);

    private static CaptureClock NewClock()
    {
        var clock = new CaptureClock(Rate, Tolerance);
        clock.Reset(Start);
        return clock;
    }

    /// <summary>Feeds packets [from, to) back to back with exact timestamps, "now" = end of each packet.</summary>
    private static void Feed(CaptureClock clock, long from, long to)
    {
        for (var f = from; f < to; f += Packet)
        {
            var p = clock.Place(Packet, T(f), timestampValid: true, discontinuity: false, nowTicks: T(f + Packet));
            Assert.Equal(new PacketPlacement(0, 0), p);
        }
    }

    [Fact]
    public void FramesAt_MapsTicksToFrames()
    {
        var clock = NewClock();

        Assert.Equal(0, clock.FramesAt(Start - 1_000_000));
        Assert.Equal(0, clock.FramesAt(Start));
        Assert.Equal(Rate, clock.FramesAt(Start + CaptureClock.TicksPerSecond));
        Assert.Equal(3600L * Rate, clock.FramesAt(Start + (3600 * CaptureClock.TicksPerSecond)));
        Assert.Equal(12_345, clock.FramesAt(T(12_345)));
    }

    [Fact]
    public void FirstPacket_IsPlacedExactlyAtItsTimestamp()
    {
        var clock = NewClock();

        var p = clock.Place(Packet, T(100), true, false, T(700));

        Assert.Equal(new PacketPlacement(100, 0), p);
        Assert.Equal(100 + Packet, clock.EmittedFrames);
    }

    [Fact]
    public void FirstPacket_FramesBeforeStartAreDropped()
    {
        var clock = NewClock();

        var p = clock.Place(Packet, Start - (100L * CaptureClock.TicksPerSecond / Rate), true, false, T(400));

        Assert.Equal(new PacketPlacement(0, 100), p);
        Assert.Equal(Packet - 100, clock.EmittedFrames);
    }

    [Fact]
    public void ContinuousStream_WithJitter_IsNotCorrected()
    {
        var clock = NewClock();
        Feed(clock, 0, Packet);
        var jitter = new Random(42);
        long position = Packet;
        for (var i = 0; i < 1_000; i++)
        {
            var stamp = position + jitter.Next(-Tolerance + 1, Tolerance);
            var p = clock.Place(Packet, T(stamp), true, false, T(position + Packet + Tolerance));
            Assert.Equal(new PacketPlacement(0, 0), p);
            position += Packet;
        }

        Assert.Equal(position, clock.EmittedFrames);
    }

    [Fact]
    public void GapBeyondTolerance_IsFilledToTheExactFrame()
    {
        var clock = NewClock();
        Feed(clock, 0, 4_800);

        var p = clock.Place(Packet, T(4_800 + 5_000), true, false, T(4_800 + 5_000 + Packet));

        Assert.Equal(new PacketPlacement(5_000, 0), p);
        Assert.Equal(4_800 + 5_000 + Packet, clock.EmittedFrames);
    }

    [Fact]
    public void GapWithinTolerance_IsAccepted()
    {
        var clock = NewClock();
        Feed(clock, 0, 4_800);

        var p = clock.Place(Packet, T(4_800 + Tolerance), true, false, T(4_800 + Tolerance + Packet));

        Assert.Equal(new PacketPlacement(0, 0), p);
    }

    [Fact]
    public void OverlapBeyondTolerance_DropsOverlappingFrames()
    {
        var clock = NewClock();
        Feed(clock, 0, 9_600);

        // A packet stamped 2 000 frames in the past (e.g. a device clock running fast).
        var p = clock.Place(Packet * 5, T(9_600 - 2_000), true, false, T(9_600 + 400));

        Assert.Equal(new PacketPlacement(0, 2_000), p);
        Assert.Equal(9_600 - 2_000 + (Packet * 5), clock.EmittedFrames);
    }

    [Fact]
    public void OverlapLargerThanPacket_DropsWholePacket()
    {
        var clock = NewClock();
        Feed(clock, 0, 9_600);

        var p = clock.Place(Packet, T(9_600 - 4_000), true, false, T(9_600));

        Assert.Equal(new PacketPlacement(0, Packet), p);
        Assert.Equal(9_600, clock.EmittedFrames);
    }

    [Fact]
    public void Discontinuity_ForcesExactPlacement()
    {
        var clock = NewClock();
        Feed(clock, 0, 4_800);

        // 300 frames would be within tolerance, but the device reported a glitch: place exactly.
        var p = clock.Place(Packet, T(4_800 + 300), true, discontinuity: true, T(4_800 + 300 + Packet));

        Assert.Equal(new PacketPlacement(300, 0), p);
    }

    [Fact]
    public void IdleWithoutPackets_FillsUpToNowMinusTolerance_NeverAhead()
    {
        var clock = NewClock();

        Assert.Equal(0, clock.IdleSilence(T(2 * Tolerance))); // not yet a gap

        for (long now = 4_800; now <= 3 * Rate; now += 4_800) // a wake-up every 100 ms
        {
            var before = clock.EmittedFrames;
            var fill = clock.IdleSilence(T(now));
            Assert.Equal(before + fill, clock.EmittedFrames);
            Assert.True(clock.EmittedFrames <= now - Tolerance, $"ahead of wall clock at {now}");
            Assert.True(now - clock.EmittedFrames <= (2 * Tolerance) + 4_800);
        }

        Assert.Equal((3 * Rate) - Tolerance, clock.EmittedFrames);
        Assert.Equal(Tolerance, clock.TailSilence(T(3 * Rate)));
        Assert.Equal(3 * Rate, clock.EmittedFrames);
    }

    [Fact]
    public void ResumeAfterIdle_IsPlacedAtThePacketTimestamp()
    {
        var clock = NewClock();
        Feed(clock, 0, Rate);                    // 1 s of sound
        clock.IdleSilence(T(2 * Rate));          // nothing plays for 1 s
        Assert.Equal((2 * Rate) - Tolerance, clock.EmittedFrames);

        // Sound resumes; its first packet was captured 10 ms after the idle fill.
        var resumeAt = (2 * Rate) + 480;
        var p = clock.Place(Packet, T(resumeAt), true, false, T(resumeAt + Packet + 200));

        Assert.Equal(new PacketPlacement(resumeAt - ((2 * Rate) - Tolerance), 0), p);
        Assert.Equal(resumeAt + Packet, clock.EmittedFrames);

        // Following packets are continuous again.
        Feed(clock, resumeAt + Packet, resumeAt + (10 * Packet));
    }

    [Fact]
    public void ResumeAfterIdle_PacketAlreadyInFlight_IsAlignedByDroppingTheOverlap()
    {
        var clock = NewClock();
        clock.IdleSilence(T(Rate));
        var emitted = clock.EmittedFrames;

        // Latency larger than the tolerance margin: the packet starts 100 frames before the emitted position.
        var p = clock.Place(Packet, T(emitted - 100), true, false, T(Rate));

        Assert.Equal(new PacketPlacement(0, 100), p);
        Assert.Equal(emitted - 100 + Packet, clock.EmittedFrames);
    }

    [Fact]
    public void InvalidTimestamp_SteadyState_AssumesContinuity()
    {
        var clock = NewClock();
        Feed(clock, 0, 4_800);

        var p = clock.Place(Packet, 0, timestampValid: false, discontinuity: false, T(100_000));

        Assert.Equal(new PacketPlacement(0, 0), p);
        Assert.Equal(4_800 + Packet, clock.EmittedFrames);
    }

    [Fact]
    public void InvalidTimestamp_OnResync_EstimatesFromNow()
    {
        var clock = NewClock();

        var p = clock.Place(Packet, 0, timestampValid: false, discontinuity: false, T(10_000));

        Assert.Equal(new PacketPlacement(10_000 - Packet, 0), p);
        Assert.Equal(10_000, clock.EmittedFrames);
    }

    [Fact]
    public void FutureTimestamp_NeverPushesSilencePastNow()
    {
        var clock = NewClock();
        Feed(clock, 0, 4_800);

        var p = clock.Place(Packet, T(1_000_000), true, false, T(9_600));

        Assert.Equal(9_600 - 4_800, p.SilenceFrames);
        Assert.Equal(9_600 + Packet, clock.EmittedFrames);
    }

    [Fact]
    public void TailSilence_CompletesToNow_AndIsIdempotent()
    {
        var clock = NewClock();
        Feed(clock, 0, 4_800);

        Assert.Equal(200, clock.TailSilence(T(5_000)));
        Assert.Equal(0, clock.TailSilence(T(5_000)));
        Assert.Equal(5_000, clock.EmittedFrames);
    }

    [Theory]
    [InlineData(100)]   // device clock 100 ppm fast
    [InlineData(-100)]  // device clock 100 ppm slow
    public void OneHour_WithDeviceClockDrift_StaysOnWallClock(int ppm)
    {
        var clock = NewClock();
        double wall = 0; // wall-clock frames; a device packet of 480 frames spans 480 / (1 + ppm) wall frames
        long kept = 0;
        long silence = 0;
        long skipped = 0;
        for (var i = 0; i < 3600 * 100; i++)
        {
            var stamp = (long)wall;
            wall += Packet / (1 + (ppm / 1e6));
            var p = clock.Place(Packet, T(stamp), true, false, T((long)wall));
            silence += p.SilenceFrames;
            skipped += p.SkipFrames;
            kept += Packet - p.SkipFrames;
            Assert.InRange(clock.EmittedFrames - (long)wall, -Tolerance - Packet, Tolerance + Packet);
        }

        Assert.Equal(kept + silence, clock.EmittedFrames);
        if (ppm > 0)
        {
            Assert.True(skipped > 0 && silence == 0);
        }
        else
        {
            Assert.True(silence > 0 && skipped == 0);
        }
    }

    [Fact]
    public void RandomizedSchedule_SilenceIsNeverAheadOfWallClock()
    {
        var random = new Random(7);
        var clock = NewClock();
        long now = 0;
        long deviceFrame = 0;
        for (var i = 0; i < 20_000; i++)
        {
            now += random.Next(0, 4_800);
            if (random.Next(4) == 0)
            {
                // Idle wake-up.
                var fill = clock.IdleSilence(T(now));
                Assert.True(fill == 0 || clock.EmittedFrames == now - Tolerance, "idle fill must stop at now - tolerance");
            }
            else
            {
                // A packet captured somewhere in the recent past (or a discontinuity jump).
                deviceFrame = Math.Max(deviceFrame, now - random.Next(0, 3 * Tolerance));
                var frames = random.Next(1, 2_000);
                var before = clock.EmittedFrames;
                var p = clock.Place(frames, T(deviceFrame), true, random.Next(10) == 0, T(now));
                Assert.True(p.SilenceFrames == 0 || before + p.SilenceFrames <= now, "silence emitted past wall clock");
                Assert.InRange(p.SkipFrames, 0, frames);
                deviceFrame += frames;
                now = Math.Max(now, deviceFrame);
            }
        }
    }

    [Fact]
    public void Constructor_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureClock(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureClock(48_000, 0));
    }
}
