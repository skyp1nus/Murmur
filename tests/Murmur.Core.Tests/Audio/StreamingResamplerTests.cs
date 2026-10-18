using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Audio;

public sealed class StreamingResamplerTests
{
    public static TheoryData<int, int> Formats => new()
    {
        { 48_000, 2 },
        { 48_000, 1 },
        { 44_100, 2 },
        { 96_000, 6 },
        { 22_050, 1 },
        { 47_999, 1 }, // 16000/47999 needs the interpolated phase table
        { 8_000, 1 },  // upsampling
        { 16_000, 2 }, // downmix only
        { 16_000, 1 }, // pass-through
    };

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    public void Sine1kHz_KeepsFrequencyAndAmplitude(int rate)
    {
        var output = ResampleOneShot(Sine(rate, 2, 1_000, 0.5, seconds: 2), new AudioFormat(rate, 2));
        var steady = output.AsSpan(4_000, 24_000); // skip the first/last 0.25 s

        var amplitude = Rms(steady) * Math.Sqrt(2);
        var gainDb = 20 * Math.Log10(amplitude / 0.5);
        Assert.InRange(gainDb, -0.5, 0.5);

        var frequency = ZeroCrossingFrequency(steady, 16_000);
        Assert.InRange(frequency, 999.5, 1_000.5);
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    public void Tone12kHz_IsAttenuatedByMoreThan40dB(int rate)
    {
        var output = ResampleOneShot(Sine(rate, 2, 12_000, 0.5, seconds: 1), new AudioFormat(rate, 2));
        var steady = output.AsSpan(2_000, 12_000);

        var attenuationDb = 20 * Math.Log10(Rms(steady) / (0.5 / Math.Sqrt(2)));
        Assert.True(attenuationDb < -40, $"12 kHz only attenuated by {attenuationDb:F1} dB");
    }

    [Theory]
    [InlineData(48_000, 4_800)]
    [InlineData(44_100, 4_410)]
    [InlineData(22_050, 2_205)]
    public void Impulse_IsCentredOnItsInputTime(int rate, int impulseIndex)
    {
        var input = new float[rate / 2];
        input[impulseIndex] = 1f;

        var output = ResampleOneShot(input, new AudioFormat(rate, 1));

        var peak = 0;
        for (var i = 1; i < output.Length; i++)
        {
            if (output[i] > output[peak])
            {
                peak = i;
            }
        }

        Assert.Equal(1_600, peak); // 0.1 s at 16 kHz
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Chunked_IsBitIdenticalToOneShot(int rate, int channels)
    {
        var format = new AudioFormat(rate, channels);
        var input = Noise(rate * channels, seed: rate + channels); // 1 s

        var oneShot = ResampleOneShot(input, format);

        var random = new Random(42);
        var resampler = new StreamingResampler(format);
        var chunked = new List<float>();
        var output = new float[resampler.MaxOutputSamples(5_000) + resampler.MaxFlushSamples];
        var offset = 0;
        while (offset < input.Length / channels)
        {
            var frames = Math.Min(random.Next(0, 5_000), (input.Length / channels) - offset);
            var n = resampler.Process(input.AsSpan(offset * channels, frames * channels), output);
            chunked.AddRange(output.AsSpan(0, n));
            offset += frames;
        }

        chunked.AddRange(output.AsSpan(0, resampler.Flush(output)));

        Assert.Equal(oneShot.Length, chunked.Count);
        Assert.True(oneShot.AsSpan().SequenceEqual(chunked.ToArray()), "chunked output differs from one-shot output");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Flush_ProducesExactlyCeilOfTheRatio(int rate, int channels)
    {
        var format = new AudioFormat(rate, channels);
        const int frames = 12_345;
        var output = ResampleOneShot(new float[frames * channels], format);

        Assert.Equal((long)Math.Ceiling(frames * 16_000.0 / rate), output.Length);
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    public void OneHour_ProducesExactlyOneHourOfOutput_WithoutDrift(int rate)
    {
        var format = new AudioFormat(rate, 2);
        var resampler = new StreamingResampler(format);
        var random = new Random(rate);
        var chunk = new float[(rate / 50) * 2]; // up to 20 ms
        var output = new float[resampler.MaxOutputSamples(rate / 50) + resampler.MaxFlushSamples];
        var totalFrames = 3_600L * rate;
        long fed = 0;
        long produced = 0;
        var maxLag = 0.0;

        while (fed < totalFrames)
        {
            var frames = (int)Math.Min(random.Next(1, rate / 50), totalFrames - fed);
            produced += resampler.Process(chunk.AsSpan(0, frames * 2), output);
            fed += frames;
            maxLag = Math.Max(maxLag, Math.Abs((fed * 16_000.0 / rate) - produced));
        }

        Assert.True(maxLag <= resampler.MaxFlushSamples, $"lag {maxLag} > {resampler.MaxFlushSamples}");
        produced += resampler.Flush(output);

        Assert.Equal(3_600L * 16_000, produced);
        Assert.Equal(produced, resampler.OutputSamples);
        Assert.Equal(totalFrames, resampler.InputFrames);
    }

    [Fact]
    public void PassThrough_CopiesMono16k()
    {
        var resampler = new StreamingResampler(AudioFormat.Asr);
        float[] input = [0.1f, -0.2f, 0.3f];
        var output = new float[resampler.MaxOutputSamples(3)];

        Assert.Equal(3, resampler.Process(input, output));
        Assert.Equal(input, output[..3]);
        Assert.Equal(0, resampler.Taps);
    }

    [Fact]
    public void Downmix_AveragesChannels()
    {
        var mono = new float[2];
        StreamingResampler.Downmix([1f, 0f, 0.5f, 0.5f, 0.25f, -0.25f], 3, mono);

        Assert.Equal(0.5f, mono[0], 6);    // (1 + 0 + 0.5) / 3
        Assert.Equal(1f / 6, mono[1], 6);  // (0.5 + 0.25 - 0.25) / 3
    }

    [Fact]
    public void Kernel_HasAtLeast16ZeroCrossingsPerSide()
    {
        var resampler = new StreamingResampler(new AudioFormat(48_000, 2));

        // halfTaps * 2 * cutoff(cycles/sample) >= 16, cutoff = 0.45 * 16000 / 48000.
        Assert.True(resampler.Taps / 2 * 2 * 0.15 >= StreamingResampler.ZeroCrossings);
    }

    [Fact]
    public void ProcessAfterFlush_Throws_UntilReset()
    {
        var resampler = new StreamingResampler(new AudioFormat(48_000, 1));
        var output = new float[1_000];
        resampler.Process(new float[300], output);
        resampler.Flush(output);

        Assert.Throws<InvalidOperationException>(() => resampler.Process(new float[3], output));

        resampler.Reset();
        Assert.Equal(0, resampler.InputFrames);
        resampler.Process(new float[300], output);
        Assert.Equal(300, resampler.InputFrames);
    }

    [Fact]
    public void TooSmallOutput_Throws()
    {
        var resampler = new StreamingResampler(new AudioFormat(48_000, 1));

        Assert.Throws<ArgumentException>(() => resampler.Process(new float[480], new float[10]));
        var stereo = new StreamingResampler(new AudioFormat(48_000, 2));
        Assert.Throws<ArgumentException>(() => stereo.Process(new float[3], new float[1_000])); // not whole frames
    }

    [Theory]
    [InlineData(48_000, 2)]
    [InlineData(44_100, 2)]
    [InlineData(47_999, 1)]
    public void Process_DoesNotAllocate(int rate, int channels)
    {
        var resampler = new StreamingResampler(new AudioFormat(rate, channels));
        var input = Noise((rate / 100) * channels, seed: 7);
        var output = new float[resampler.MaxOutputSamples(rate / 100)];
        resampler.Process(input, output);

        var allocated = Allocations.SteadyState(() =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                resampler.Process(input, output);
            }
        });
        Assert.Equal(0, allocated);
    }

    internal static float[] ResampleOneShot(float[] interleaved, AudioFormat format)
    {
        var resampler = new StreamingResampler(format);
        var frames = interleaved.Length / format.Channels;
        var output = new float[resampler.MaxOutputSamples(frames) + resampler.MaxFlushSamples];
        var n = resampler.Process(interleaved, output);
        n += resampler.Flush(output.AsSpan(n));
        return output[..n];
    }

    private static float[] Sine(int rate, int channels, double frequency, double amplitude, double seconds)
    {
        var frames = (int)(rate * seconds);
        var samples = new float[frames * channels];
        for (var f = 0; f < frames; f++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * f / rate));
            for (var c = 0; c < channels; c++)
            {
                samples[(f * channels) + c] = v;
            }
        }

        return samples;
    }

    private static float[] Noise(int count, int seed)
    {
        var random = new Random(seed);
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = (float)((random.NextDouble() * 2) - 1);
        }

        return samples;
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        var sum = 0.0;
        foreach (var s in samples)
        {
            sum += (double)s * s;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>Frequency from the interpolated times of the first and last rising zero crossings.</summary>
    private static double ZeroCrossingFrequency(ReadOnlySpan<float> samples, int rate)
    {
        double first = -1, last = -1;
        var crossings = 0;
        for (var i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0)
            {
                var t = (i - 1) + (samples[i - 1] / (double)(samples[i - 1] - samples[i]));
                if (first < 0)
                {
                    first = t;
                }

                last = t;
                crossings++;
            }
        }

        return (crossings - 1) / ((last - first) / rate);
    }
}
