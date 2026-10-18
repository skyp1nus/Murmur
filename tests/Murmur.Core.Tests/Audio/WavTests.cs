using Murmur.Core.Audio;
using Murmur.Core.Tests.Fakes;
using System.Buffers.Binary;

namespace Murmur.Core.Tests.Audio;

public sealed class WavTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Header_HasCanonicalPcm16Fields()
    {
        var path = _dir.File("a.wav");
        using (var writer = new WavPcm16Writer(path))
        {
            writer.Append(new float[1_000]);
        }

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(44 + 2_000, bytes.Length);
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal(36u + 2_000, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal("WAVEfmt "u8.ToArray(), bytes[8..16]);
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20)));       // PCM
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)));       // mono
        Assert.Equal(16_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24))); // rate
        Assert.Equal(32_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28))); // byte rate
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(32)));       // block align
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)));      // bits
        Assert.Equal("data"u8.ToArray(), bytes[36..40]);
        Assert.Equal(2_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
    }

    [Fact]
    public void Append_ClipsAndMapsNaNToZero()
    {
        var path = _dir.File("clip.wav");
        using (var writer = new WavPcm16Writer(path))
        {
            writer.Append([0f, 0.5f, -0.5f, 1f, -1f, 2f, -3f, float.NaN, float.PositiveInfinity, float.NegativeInfinity]);
            Assert.Equal(10, writer.SamplesWritten);
        }

        var bytes = File.ReadAllBytes(path).AsSpan(44);
        short[] expected = [0, 16384, -16384, 32767, -32767, 32767, -32767, 0, 32767, -32767];
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], BinaryPrimitives.ReadInt16LittleEndian(bytes[(2 * i)..]));
        }
    }

    [Fact]
    public void RoundTrip_ThroughWavReader()
    {
        var path = _dir.File("rt.wav");
        var samples = new float[50_000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = MathF.Sin(i * 0.05f) * 0.8f;
        }

        using (var writer = new WavPcm16Writer(path))
        {
            writer.Append(samples);
            Assert.Equal(TimeSpan.FromSeconds(50_000 / 16_000.0), writer.Duration);
        }

        var (format, read) = WavReader.ReadAll(path);
        Assert.Equal(new Murmur.Core.Capture.AudioFormat(16_000, 1), format);
        Assert.Equal(samples.Length, read.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.Equal(samples[i], read[i], 1e-4f);
        }
    }

    [Fact]
    public void FlushToDisk_MakesHeaderAndDataVisibleWhileTheWriterIsOpen()
    {
        var path = _dir.File("flush.wav");
        using var writer = new WavPcm16Writer(path);
        writer.Append(new float[16_000]);

        // Before any header update the header still says 0 bytes.
        using (var stale = WavReader.Open(path))
        {
            Assert.Equal(0, stale.Layout.DeclaredDataBytes);
        }

        writer.FlushToDisk();

        using var reader = WavReader.Open(path);
        Assert.Equal(32_000, reader.Layout.DeclaredDataBytes);
        Assert.Equal(16_000, reader.FrameCount);
        Assert.Equal(TimeSpan.FromSeconds(1), reader.Duration);
    }

    [Fact]
    public void Flush_WritesDataAndHeaderInTheBackground()
    {
        var path = _dir.File("bg-flush.wav");
        using var writer = new WavPcm16Writer(path);
        writer.Append(new float[16_000]);

        writer.Flush();

        WaitForDeclaredBytes(path, 32_000);
        Assert.Equal(44 + 32_000, new FileInfo(path).Length);
    }

    [Fact]
    public void UpdateHeader_RewritesTheSizes()
    {
        var path = _dir.File("header.wav");
        using var writer = new WavPcm16Writer(path);
        writer.Append(new float[800]);

        writer.UpdateHeader();

        WaitForDeclaredBytes(path, 1_600);
        Assert.Equal(44 + 1_600, new FileInfo(path).Length);
    }

    [Fact]
    public void StreamWriter_UpdateHeaderAndFlush_AreSynchronous()
    {
        using var stream = new MemoryStream();
        using var writer = new WavPcm16Writer(stream, leaveOpen: true);
        writer.Append(new float[800]);

        writer.UpdateHeader();
        Assert.Equal(1_600u, BinaryPrimitives.ReadUInt32LittleEndian(stream.GetBuffer().AsSpan(40)));

        writer.Append(new float[200]);
        writer.Flush();
        Assert.Equal(2_000u, BinaryPrimitives.ReadUInt32LittleEndian(stream.GetBuffer().AsSpan(40)));
        Assert.Equal(44 + 2_000, stream.Length);
    }

    [Fact]
    public async Task FlushToDisk_CanRunConcurrentlyWithAppend()
    {
        var path = _dir.File("concurrent.wav");
        var samples = Ramp(16_000);
        using var writer = new WavPcm16Writer(path);
        using var stop = new CancellationTokenSource();
        var flushes = 0;
        var flusher = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                writer.FlushToDisk();
                Interlocked.Increment(ref flushes);
            }
        }, TestContext.Current.CancellationToken);

        while (Volatile.Read(ref flushes) == 0)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        for (var i = 0; i < 20; i++)
        {
            writer.Append(samples);
            writer.UpdateHeader();
        }

        await stop.CancelAsync();
        await flusher;
        writer.Dispose();

        Assert.True(flushes > 0);
        var (_, read) = WavReader.ReadAll(path);
        Assert.Equal(20 * samples.Length, read.Length);
        for (var i = 0; i < read.Length; i += 997)
        {
            Assert.Equal(samples[i % samples.Length], read[i], 1e-4f);
        }
    }

    [Fact]
    public void Repair_FixesStaleHeaderAndDropsPartialSample()
    {
        var path = _dir.File("crash.wav");
        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 0);
        var writer = new WavPcm16Writer(stream, leaveOpen: true);
        writer.Append(Ramp(16_000));
        writer.Flush();
        writer.Append(Ramp(8_000)); // never flushed: the header still says 1 s
        stream.WriteByte(0x7F);     // half a sample, as if the process died mid-write
        stream.Dispose();           // "crash": the writer never disposes

        using (var before = WavReader.Open(path))
        {
            Assert.Equal(16_000, before.FrameCount);
        }

        Assert.True(WavRepair.TryRepair(path, out var duration));
        Assert.Equal(TimeSpan.FromSeconds(1.5), duration);
        Assert.Equal(44 + (24_000 * 2), new FileInfo(path).Length);

        var (_, samples) = WavReader.ReadAll(path);
        Assert.Equal(24_000, samples.Length);
        var ramp = Ramp(8_000);
        Assert.Equal(ramp[^1], samples[^1], 1e-4f);
    }

    [Fact]
    public void Repair_HeaderOnlyFile_HasZeroDuration()
    {
        var path = _dir.File("empty.wav");
        new WavPcm16Writer(path).Dispose();

        Assert.True(WavRepair.TryRepair(path, out var duration));
        Assert.Equal(TimeSpan.Zero, duration);
    }

    [Fact]
    public void Repair_RejectsMissingAndInvalidFiles()
    {
        Assert.False(WavRepair.TryRepair(_dir.File("missing.wav"), out _));

        var garbage = _dir.File("garbage.wav");
        File.WriteAllBytes(garbage, new byte[100]);
        Assert.False(WavRepair.TryRepair(garbage, out _));

        var truncated = _dir.File("truncated.wav");
        File.WriteAllBytes(truncated, "RIFF\0\0\0\0WAVEfmt "u8.ToArray());
        Assert.False(WavRepair.TryRepair(truncated, out _));
    }

    [Theory]
    [InlineData(1, 8, 22_050, 1)]
    [InlineData(1, 16, 44_100, 2)]
    [InlineData(1, 24, 48_000, 2)]
    [InlineData(1, 32, 8_000, 1)]
    [InlineData(3, 32, 48_000, 2)]
    [InlineData(3, 64, 16_000, 1)]
    public void Reader_DecodesPcmAndFloatFormats(int tag, int bits, int rate, int channels)
    {
        var path = _dir.File($"fmt-{tag}-{bits}.wav");
        float[] values = [0f, 0.5f, -0.5f, 0.25f, -1f, 0.75f];
        WriteWav(path, tag, bits, rate, channels, values, extensible: false);

        using var reader = WavReader.Open(path);
        Assert.Equal(rate, reader.Format.SampleRate);
        Assert.Equal(channels, reader.Format.Channels);
        Assert.Equal(values.Length / channels, reader.FrameCount);
        var read = new float[values.Length];
        Assert.Equal(values.Length / channels, reader.Read(read));
        for (var i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], read[i], bits == 8 ? 0.01f : 1e-4f);
        }
    }

    [Fact]
    public void Reader_HandlesWaveFormatExtensibleAndExtraChunks()
    {
        var path = _dir.File("ext.wav");
        float[] values = [0.1f, -0.2f, 0.3f, -0.4f];
        WriteWav(path, 3, 32, 48_000, 2, values, extensible: true);

        var (format, samples) = WavReader.ReadAll(path);

        Assert.Equal(new Murmur.Core.Capture.AudioFormat(48_000, 2), format);
        Assert.Equal(values, samples);
    }

    [Fact]
    public void Reader_RejectsNonWav()
    {
        var path = _dir.File("x.wav");
        File.WriteAllBytes(path, new byte[64]);

        Assert.Throws<InvalidDataException>(() => WavReader.Open(path));
    }

    [Fact]
    public void AppendAndFlush_DoNotAllocate()
    {
        using var writer = new WavPcm16Writer(Stream.Null);
        var chunk = new float[160];
        writer.Append(chunk);
        writer.Flush();

        var allocated = Allocations.SteadyState(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                writer.Append(chunk);
            }

            writer.Flush();
        });
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void FileAppendAndFlush_DoNotAllocate()
    {
        using var writer = new WavPcm16Writer(_dir.File("alloc.wav"));
        var chunk = new float[320];
        writer.Append(chunk);
        writer.Flush();

        var allocated = Allocations.SteadyState(() =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                writer.Append(chunk);
                if (i % 100 == 0)
                {
                    writer.Flush();
                }
            }
        });
        Assert.Equal(0, allocated);
    }

    /// <summary>Polls the header of a file that a background I/O thread is updating.</summary>
    private static void WaitForDeclaredBytes(string path, long expected)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            using (var reader = WavReader.Open(path))
            {
                if (reader.Layout.DeclaredDataBytes == expected)
                {
                    return;
                }

                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"header still declares {reader.Layout.DeclaredDataBytes} bytes");
            }

            Thread.Sleep(5);
        }
    }

    private static float[] Ramp(int count)
    {
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = ((i % 1000) / 1000f) - 0.5f;
        }

        return samples;
    }

    private static void WriteWav(string path, int tag, int bits, int rate, int channels, float[] values, bool extensible)
    {
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        var bytesPerSample = bits / 8;
        var dataBytes = values.Length * bytesPerSample;
        var fmtSize = extensible ? 40 : 16;
        var junk = new byte[6];

        w.Write("RIFF"u8);
        w.Write(4 + (8 + fmtSize) + (8 + junk.Length) + (8 + dataBytes));
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(fmtSize);
        w.Write((ushort)(extensible ? 0xFFFE : tag));
        w.Write((ushort)channels);
        w.Write(rate);
        w.Write(rate * channels * bytesPerSample);
        w.Write((ushort)(channels * bytesPerSample));
        w.Write((ushort)bits);
        if (extensible)
        {
            w.Write((ushort)22);
            w.Write((ushort)bits);
            w.Write(3); // channel mask
            w.Write((ushort)tag);
            w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
        }

        w.Write("LIST"u8);
        w.Write(junk.Length);
        w.Write(junk);

        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var v in values)
        {
            switch (tag, bits)
            {
                case (1, 8):
                    w.Write((byte)Math.Clamp(Math.Round((v * 128) + 128), 0, 255));
                    break;
                case (1, 16):
                    w.Write((short)Math.Clamp(Math.Round(v * 32768), short.MinValue, short.MaxValue));
                    break;
                case (1, 24):
                    var s24 = (int)Math.Clamp(Math.Round(v * 8388608), -8388608, 8388607);
                    w.Write((byte)s24);
                    w.Write((byte)(s24 >> 8));
                    w.Write((byte)(s24 >> 16));
                    break;
                case (1, 32):
                    w.Write((int)Math.Clamp(Math.Round(v * 2147483648.0), int.MinValue, int.MaxValue));
                    break;
                case (3, 32):
                    w.Write(v);
                    break;
                case (3, 64):
                    w.Write((double)v);
                    break;
            }
        }
    }
}
