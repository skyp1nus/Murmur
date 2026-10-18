using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Audio;

namespace Murmur.Capture.Windows.Tests;

public sealed class SampleConverterTests
{
    [Fact]
    public void Float32_IsCopiedBitExact()
    {
        float[] samples = [0f, 0.5f, -0.25f, 1f, -1f, 1e-7f];
        var bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        var dst = new float[samples.Length];

        SampleConverter.ToFloat(bytes, SampleType.Float32, dst);

        Assert.Equal(samples, dst);
    }

    [Fact]
    public void Pcm16_ScalesBy32768()
    {
        short[] samples = [0, short.MinValue, short.MaxValue, 16384, -16384, 1];
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        }

        var dst = new float[samples.Length];
        SampleConverter.ToFloat(bytes, SampleType.Pcm16, dst);

        Assert.Equal(0f, dst[0]);
        Assert.Equal(-1f, dst[1]);
        Assert.Equal(32767f / 32768f, dst[2]);
        Assert.Equal(0.5f, dst[3]);
        Assert.Equal(-0.5f, dst[4]);
        Assert.Equal(1f / 32768f, dst[5]);
    }

    [Fact]
    public void Pcm24_SignExtendsPackedSamples()
    {
        int[] samples = [0, -8_388_608, 8_388_607, 4_194_304, -1, 1, -4_194_304];
        var bytes = new byte[samples.Length * 3];
        for (var i = 0; i < samples.Length; i++)
        {
            bytes[(i * 3) + 0] = (byte)samples[i];
            bytes[(i * 3) + 1] = (byte)(samples[i] >> 8);
            bytes[(i * 3) + 2] = (byte)(samples[i] >> 16);
        }

        var dst = new float[samples.Length];
        SampleConverter.ToFloat(bytes, SampleType.Pcm24, dst);

        for (var i = 0; i < samples.Length; i++)
        {
            Assert.Equal(samples[i] / 8_388_608f, dst[i]);
        }

        Assert.Equal(-1f, dst[1]);
        Assert.Equal(0.5f, dst[3]);
        Assert.Equal(-0.5f, dst[6]);
    }

    [Fact]
    public void Pcm32_ScalesBy2Pow31_AndHandles24In32()
    {
        int[] samples = [0, int.MinValue, int.MaxValue, 1 << 30, 0x40000000 >> 8 << 8, unchecked((int)0xFFFFFF00)];
        var bytes = new byte[samples.Length * 4];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), samples[i]);
        }

        var dst = new float[samples.Length];
        SampleConverter.ToFloat(bytes, SampleType.Pcm32, dst);

        Assert.Equal(0f, dst[0]);
        Assert.Equal(-1f, dst[1]);
        Assert.Equal(1f, dst[2], 6); // 2^31-1 rounds to 1.0 in float
        Assert.Equal(0.5f, dst[3]);
        Assert.Equal(0.5f, dst[4]);
        Assert.Equal(-1f / 8_388_608f, dst[5]); // 24-bit "-1" left-justified in 32 bits
    }

    [Fact]
    public void Interleaving_IsPreserved()
    {
        // Stereo PCM16: left = +0.5, right = -0.25 on every frame.
        var bytes = new byte[4 * 3];
        for (var f = 0; f < 3; f++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(f * 4), 16384);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan((f * 4) + 2), -8192);
        }

        var dst = new float[6];
        SampleConverter.ToFloat(bytes, SampleType.Pcm16, dst);

        Assert.Equal([0.5f, -0.25f, 0.5f, -0.25f, 0.5f, -0.25f], dst);
    }

    [Fact]
    public void LengthMismatch_Throws() =>
        Assert.Throws<ArgumentException>(() => SampleConverter.ToFloat(new byte[6], SampleType.Pcm16, new float[4]));

    [Theory]
    [InlineData((int)SampleType.Float32, 4)]
    [InlineData((int)SampleType.Pcm16, 2)]
    [InlineData((int)SampleType.Pcm24, 3)]
    [InlineData((int)SampleType.Pcm32, 4)]
    public void BytesPerSample_MatchesContainer(int type, int bytes) =>
        Assert.Equal(bytes, SampleConverter.BytesPerSample((SampleType)type));

    [Theory]
    [InlineData((int)SampleType.Float32)]
    [InlineData((int)SampleType.Pcm16)]
    [InlineData((int)SampleType.Pcm24)]
    [InlineData((int)SampleType.Pcm32)]
    public void ToFloat_DoesNotAllocate(int sampleType)
    {
        var type = (SampleType)sampleType;
        const int Samples = 960; // 10 ms of 48 kHz stereo
        var src = new byte[Samples * SampleConverter.BytesPerSample(type)];
        var dst = new float[Samples];
        SampleConverter.ToFloat(src, type, dst); // warm up (JIT, statics)

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            SampleConverter.ToFloat(src, type, dst);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
