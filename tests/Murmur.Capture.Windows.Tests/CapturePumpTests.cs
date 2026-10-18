using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Audio;
using Murmur.Capture.Windows.Audio.Interop;

namespace Murmur.Capture.Windows.Tests;

public sealed class CapturePumpTests
{
    private const int Rate = 48_000;
    private const int Packet = 480;
    private const int Tolerance = 1_920;
    private const long Start = 50_000_000;

    private static long T(long frames) => Ticks.At(frames, Rate, Start);

    private static byte[] Pcm16Stereo(int frames, Func<int, (short L, short R)> sample)
    {
        var bytes = new byte[frames * 4];
        for (var f = 0; f < frames; f++)
        {
            var (l, r) = sample(f);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(f * 4), l);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan((f * 4) + 2), r);
        }

        return bytes;
    }

    private static byte[] Float32Mono(int frames, Func<int, float> sample)
    {
        var samples = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            samples[f] = sample(f);
        }

        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }

    [Fact]
    public void Drain_ConvertsPcm16Packets_AndReleasesEach()
    {
        var format = WaveFormatBytes.Pcm16();
        var pump = new CapturePump(format, Packet, Tolerance);
        var sink = new RecordingSink(2);
        using var client = new FakeCaptureClient(8, Packet * 4);
        pump.Start(sink, Start);
        client.Enqueue(Pcm16Stereo(Packet, _ => (16384, -8192)), Packet, 0, T(0));
        client.Enqueue(Pcm16Stereo(Packet, _ => (-16384, 8192)), Packet, 0, T(Packet));

        Assert.Equal(0, pump.Drain(client, T(2 * Packet)));

        Assert.Equal(2, client.ReleaseCalls);
        Assert.Equal((uint)Packet, client.LastReleasedFrames);
        Assert.Equal(0, client.Queued);
        Assert.Equal(2 * Packet, sink.Frames);
        Assert.Equal(0.5f, sink.Samples[0]);
        Assert.Equal(-0.25f, sink.Samples[1]);
        Assert.Equal(-0.5f, sink.Samples[2 * Packet]);
        Assert.Equal(2, pump.Packets);
    }

    [Fact]
    public void SilentFlag_EmitsWriteSilence_WithoutReadingData()
    {
        var pump = new CapturePump(WaveFormatBytes.Float32(1), Packet, Tolerance);
        var sink = new RecordingSink(1);
        using var client = new FakeCaptureClient(4, Packet * 4);
        pump.Start(sink, Start);
        client.Enqueue(Float32Mono(Packet, _ => 0.9f), Packet, WasapiConstants.AudclntBufferFlagsSilent, T(0));

        Assert.Equal(0, pump.Drain(client, T(Packet)));

        Assert.Equal([(true, Packet)], sink.Calls);
        Assert.Equal(1, pump.SilentPackets);
        Assert.All(sink.Samples, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void PacketLargerThanChunk_IsWrittenInChunks()
    {
        var pump = new CapturePump(WaveFormatBytes.Float32(1), 100, Tolerance);
        var sink = new RecordingSink(1);
        using var client = new FakeCaptureClient(2, 1_000 * 4);
        pump.Start(sink, Start);
        client.Enqueue(Float32Mono(1_000, f => f / 1_000f), 250, 0, T(0));

        Assert.Equal(0, pump.Drain(client, T(250)));

        Assert.Equal([(false, 100), (false, 100), (false, 50)], sink.Calls);
        for (var i = 0; i < 250; i++)
        {
            Assert.Equal(i / 1_000f, sink.Samples[i]);
        }
    }

    [Fact]
    public void Drain_ReturnsGetBufferFailure()
    {
        var pump = new CapturePump(WaveFormatBytes.Float32(1), Packet, Tolerance);
        using var client = new FakeCaptureClient(2, Packet * 4) { GetBufferResult = WasapiConstants.AudclntEDeviceInvalidated };
        pump.Start(new RecordingSink(1), Start);

        Assert.Equal(WasapiConstants.AudclntEDeviceInvalidated, pump.Drain(client, T(0)));
        Assert.Equal(0, client.ReleaseCalls);
    }

    [Fact]
    public void Drain_ReturnsReleaseBufferFailure()
    {
        var pump = new CapturePump(WaveFormatBytes.Float32(1), Packet, Tolerance);
        using var client = new FakeCaptureClient(2, Packet * 4) { ReleaseBufferResult = unchecked((int)0x88890007) };
        pump.Start(new RecordingSink(1), Start);
        client.Enqueue(Float32Mono(Packet, _ => 0f), Packet, 0, T(0));
        client.Enqueue(Float32Mono(Packet, _ => 0f), Packet, 0, T(Packet));

        Assert.Equal(unchecked((int)0x88890007), pump.Drain(client, T(2 * Packet)));
        Assert.Equal(1, client.ReleaseCalls);
    }

    [Fact]
    public void IdleLoopback_ThreeSeconds_DeliversExactlyThreeSecondsOfFrames()
    {
        var pump = new CapturePump(WaveFormatBytes.Float32(2), Packet, Tolerance);
        var sink = new CountingSink(2);
        using var client = new FakeCaptureClient(2, Packet * 8);
        pump.Start(sink, Start);

        // Nothing plays: every 100 ms the wait times out, nothing to drain.
        for (long now = 4_800; now < 3 * Rate; now += 4_800)
        {
            Assert.Equal(0, pump.Drain(client, T(now)));
            pump.Idle(T(now));
        }

        pump.Finish(T(3 * Rate));

        Assert.Equal(3 * Rate, sink.Frames);
        Assert.Equal(3 * Rate, sink.SilenceFrames);
    }

    [Fact]
    public void LoopbackResume_RealAudioLandsAtItsWallClockPosition()
    {
        var pump = new CapturePump(WaveFormatBytes.Float32(1), Packet, Tolerance);
        var sink = new RecordingSink(1);
        using var client = new FakeCaptureClient(8, Packet * 4);
        pump.Start(sink, Start);

        // 1.2 s of nothing.
        for (long now = 4_800; now <= 57_600; now += 4_800)
        {
            pump.Drain(client, T(now));
            pump.Idle(T(now));
        }

        // Sound starts at frame 58 000; packets arrive 10 ms later.
        const int SoundStart = 58_000;
        for (var k = 0; k < 5; k++)
        {
            var first = SoundStart + (k * Packet);
            client.Enqueue(Float32Mono(Packet, f => (first + f) / 1e6f), Packet, 0, T(first));
            pump.Drain(client, T(first + Packet + 480));
            pump.Idle(T(first + Packet + 480));
        }

        var firstNonZero = sink.Samples.FindIndex(s => s != 0f);
        Assert.Equal(SoundStart, firstNonZero);
        Assert.Equal(SoundStart / 1e6f, sink.Samples[SoundStart]);
        Assert.Equal((SoundStart + (5 * Packet) - 1) / 1e6f, sink.Samples[SoundStart + (5 * Packet) - 1]);
        Assert.Equal(SoundStart + (5 * Packet), sink.Frames);
    }

    [Fact]
    public void Discontinuity_IsCountedAndPlacedExactly()
    {
        var pump = new CapturePump(WaveFormatBytes.Float32(1), Packet, Tolerance);
        var sink = new RecordingSink(1);
        using var client = new FakeCaptureClient(4, Packet * 4);
        pump.Start(sink, Start);
        client.Enqueue(Float32Mono(Packet, _ => 0.1f), Packet, 0, T(0));
        client.Enqueue(Float32Mono(Packet, _ => 0.2f), Packet, WasapiConstants.AudclntBufferFlagsDataDiscontinuity, T(Packet + 200));

        pump.Drain(client, T((2 * Packet) + 200));

        Assert.Equal(1, pump.Discontinuities);
        Assert.Equal(200, pump.GapFrames);
        Assert.Equal((2 * Packet) + 200, sink.Frames);
        Assert.Equal(0f, sink.Samples[Packet + 199]);
        Assert.Equal(0.2f, sink.Samples[Packet + 200]);
    }

    [Fact]
    public void OverlappingPacket_SkipsLeadingFrames_ConvertingFromTheRightOffset()
    {
        var pump = new CapturePump(WaveFormatBytes.Pcm16(), Packet, Tolerance);
        var sink = new RecordingSink(2);
        using var client = new FakeCaptureClient(4, Packet * 4);
        pump.Start(sink, Start);

        // The first packet is stamped 100 frames before Start: those frames are dropped.
        client.Enqueue(Pcm16Stereo(Packet, f => ((short)f, (short)-f)), Packet, 0, Start - (100L * CaptureClock.TicksPerSecond / Rate));
        pump.Drain(client, T(Packet));

        Assert.Equal(100, pump.SkippedFrames);
        Assert.Equal(Packet - 100, sink.Frames);
        Assert.Equal(100 / 32768f, sink.Samples[0]);
        Assert.Equal(-100 / 32768f, sink.Samples[1]);
    }

    [Fact]
    public void TimestampError_PacketsAreTreatedAsContinuous()
    {
        var pump = new CapturePump(WaveFormatBytes.Float32(1), Packet, Tolerance);
        var sink = new RecordingSink(1);
        using var client = new FakeCaptureClient(4, Packet * 4);
        pump.Start(sink, Start);
        client.Enqueue(Float32Mono(Packet, _ => 0.1f), Packet, 0, T(0));
        client.Enqueue(Float32Mono(Packet, _ => 0.2f), Packet, WasapiConstants.AudclntBufferFlagsTimestampError, T(40_000));

        pump.Drain(client, T(50_000));

        Assert.Equal(2 * Packet, sink.Frames);
        Assert.Equal(0, pump.GapFrames);
    }

    [Fact]
    public void SteadyStateDrain_DoesNotAllocate()
    {
        var pump = new CapturePump(WaveFormatBytes.Float32(2), Packet, Tolerance);
        var sink = new CountingSink(2);
        using var client = new FakeCaptureClient(64, Packet * 8);
        var packet = new byte[Packet * 8];
        pump.Start(sink, Start);

        long frame = 0;
        var failures = 0;
        void Round(int packets)
        {
            for (var i = 0; i < packets; i++)
            {
                client.Enqueue(packet, Packet, i % 7 == 0 ? WasapiConstants.AudclntBufferFlagsSilent : 0, T(frame));
                frame += Packet;
            }

            if (pump.Drain(client, T(frame)) != 0)
            {
                failures++;
            }

            pump.Idle(T(frame));
        }

        Round(32); // warm up: JIT, statics

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var r = 0; r < 100; r++)
        {
            Round(32);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(0, failures);
        Assert.Equal(frame, sink.Frames);
    }
}
