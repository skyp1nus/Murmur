using Murmur.Transcription.Audio;

namespace Murmur.Transcription.Tests;

public sealed class WavReaderTests
{
    private static readonly short[] Ramp = [0, 1000, -1000, short.MaxValue, short.MinValue, 16384, -16384, 7];

    private static WavReader Open(byte[] file) => WavReader.Open(new MemoryStream(file));

    [Fact]
    public void Pcm16Mono16k_IsStreamedAsFloats()
    {
        using var reader = Open(TestWav.Build(TestWav.Pcm, 16_000, 1, 16, TestWav.Pcm16(Ramp)));

        var samples = TestWav.ReadAll(reader, 3);

        Assert.Equal(Ramp.Length, reader.TotalSamples);
        Assert.Equal(Ramp.Select(s => s / 32768f), samples);
        Assert.Equal(0, reader.Read(new float[4])); // stays at the end
    }

    [Fact]
    public void Float32Mono16k_IsReadVerbatim()
    {
        float[] values = [0f, 0.5f, -0.25f, 1f, -1f];
        using var reader = Open(TestWav.Build(TestWav.Float, 16_000, 1, 32, TestWav.Float32(values)));

        Assert.Equal(values, TestWav.ReadAll(reader, 2));
    }

    [Theory]
    [InlineData(1, 16, TestWav.Pcm)]
    [InlineData(3, 32, TestWav.Float)]
    public void WaveFormatExtensible_UsesTheSubFormat(ushort subFormat, int bits, ushort plainTag)
    {
        var data = bits == 16 ? TestWav.Pcm16(Ramp) : TestWav.Float32([0.1f, 0.2f]);
        using var ext = Open(TestWav.Build(TestWav.Extensible, 16_000, 1, bits, data, subFormat: subFormat));
        using var plain = Open(TestWav.Build(plainTag, 16_000, 1, bits, data));

        Assert.Equal(TestWav.ReadAll(plain, 64), TestWav.ReadAll(ext, 64));
    }

    [Fact]
    public void ChunksBeforeData_AreSkipped_IncludingOddPadding()
    {
        using var reader = Open(TestWav.Build(TestWav.Pcm, 16_000, 1, 16, TestWav.Pcm16(Ramp), extraChunkBeforeData: "INFOabc"u8));

        Assert.Equal(Ramp.Length, TestWav.ReadAll(reader, 5).Length);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    [InlineData(1_000_000u)]
    public void UnfinalisedOrOversizedDataSize_ReadsToEndOfFile(uint declared)
    {
        using var reader = Open(TestWav.Build(TestWav.Pcm, 16_000, 1, 16, TestWav.Pcm16(Ramp), declaredDataSize: declared));

        Assert.Equal(Ramp.Length, reader.TotalSamples);
        Assert.Equal(Ramp.Length, TestWav.ReadAll(reader, 4).Length);
    }

    [Fact]
    public void TrailingHalfSample_IsIgnored()
    {
        var data = TestWav.Pcm16(Ramp).Concat(new byte[] { 0x12 }).ToArray();
        using var reader = Open(TestWav.Build(TestWav.Pcm, 16_000, 1, 16, data));

        Assert.Equal(Ramp.Length, TestWav.ReadAll(reader, 3).Length);
    }

    [Fact]
    public void LongFile_IsReadInChunksWithoutLoss()
    {
        var samples = Enumerable.Range(0, 100_003).Select(i => (short)((i * 37) % 30000 - 15000)).ToArray();
        using var reader = Open(TestWav.Build(TestWav.Pcm, 16_000, 1, 16, TestWav.Pcm16(samples)));

        var read = TestWav.ReadAll(reader, 7_777);

        Assert.Equal(samples.Length, read.Length);
        Assert.Equal(samples[^1] / 32768f, read[^1]);
        Assert.Equal(samples[54_321] / 32768f, read[54_321]);
    }

    [Theory]
    [InlineData(TestWav.Pcm, 44_100, 1, 16, "44100 Hz")]
    [InlineData(TestWav.Pcm, 24_000, 1, 16, "24000 Hz")]
    [InlineData(TestWav.Pcm, 16_000, 2, 16, "2 ch")]
    [InlineData(TestWav.Pcm, 16_000, 1, 24, "PCM24")]
    [InlineData(TestWav.Pcm, 16_000, 1, 8, "PCM8")]
    [InlineData(TestWav.Float, 16_000, 1, 64, "float64")]
    public void UnsupportedFormats_AreRejectedWithAClearMessage(ushort tag, int rate, int channels, int bits, string expected)
    {
        var file = TestWav.Build(tag, rate, channels, bits, new byte[channels * bits / 8 * 4]);

        var ex = Assert.Throws<InvalidDataException>(() => Open(file));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains("16000 Hz mono PCM16 or float32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonWavInput_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => Open("not a wav file at all"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => Open([]));
    }

    [Fact]
    public void MissingDataChunk_IsRejected()
    {
        var file = TestWav.Build(TestWav.Pcm, 16_000, 1, 16, []);
        var truncated = file.AsSpan(0, file.Length - 8).ToArray(); // drop the "data" chunk header

        var ex = Assert.Throws<InvalidDataException>(() => Open(truncated));
        Assert.Contains("data", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFormatTag_IsRejected()
    {
        var file = TestWav.Build(0x0055 /* MP3 */, 16_000, 1, 16, new byte[8]);

        Assert.Throws<InvalidDataException>(() => Open(file));
    }

    [Fact]
    public void OpenByPath_WorksForNonAsciiPaths()
    {
        using var dir = new TempDir("кирилиця");
        var path = dir.File("запис доріжки.wav");
        File.WriteAllBytes(path, TestWav.Build(TestWav.Pcm, 16_000, 1, 16, TestWav.Pcm16(Ramp)));

        using var reader = WavReader.Open(path);

        Assert.Equal(Ramp.Length, TestWav.ReadAll(reader, 16).Length);
        Assert.Equal(Ramp.Length / 16_000.0, reader.DurationSeconds, 9);
    }

    [Fact]
    public void WavFile_EncodeThenRead_RoundTrips()
    {
        float[] samples = [0f, 0.25f, -0.5f, 0.999f];

        using var pcm = Open(WavFile.Encode(samples, 16_000));
        using var flt = Open(WavFile.Encode(samples, 16_000, asFloat: true));

        var p = TestWav.ReadAll(pcm, 8);
        Assert.All(samples.Zip(p), t => Assert.True(Math.Abs(t.First - t.Second) <= 2f / 32767f));
        Assert.Equal(samples, TestWav.ReadAll(flt, 8));
    }

    [Fact]
    public void WavFile_ReadMono_DownmixesAnyFormat()
    {
        using var dir = new TempDir();
        var path = dir.File("stereo.wav");
        File.WriteAllBytes(path, TestWav.Build(TestWav.Pcm, 44_100, 2, 16, TestWav.Pcm16([1000, 3000, -2000, 0])));

        var (mono, rate) = WavFile.ReadMono(path);

        Assert.Equal(44_100, rate);
        Assert.Equal([2000 / 32768f, -1000 / 32768f], mono);
    }

    [Fact]
    public void Resampler_PreservesALowFrequencySine()
    {
        const int inRate = 24_000;
        var input = Enumerable.Range(0, inRate).Select(i => MathF.Sin(2 * MathF.PI * 440 * i / inRate) * 0.5f).ToArray();

        var output = new SincResampler(inRate, 16_000).Process(input);

        Assert.Equal(16_000, output.Length);
        var maxErr = 0.0;
        for (var n = 200; n < output.Length - 200; n++)
        {
            maxErr = Math.Max(maxErr, Math.Abs(output[n] - (Math.Sin(2 * Math.PI * 440 * n / 16_000.0) * 0.5)));
        }

        Assert.True(maxErr < 1e-3, $"max error {maxErr}");
    }
}
