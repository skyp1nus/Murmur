using Murmur.Capture.Windows.Playback;

namespace Murmur.Capture.Windows.Tests;

/// <summary>An in-memory <see cref="IPcmSource"/>.</summary>
internal sealed class ArraySource(short[] samples) : IPcmSource
{
    public long TotalFrames => samples.Length;

    public long Position { get; private set; }

    public int Render(Span<short> destination)
    {
        var count = (int)Math.Min(destination.Length, samples.Length - Position);
        samples.AsSpan((int)Position, count).CopyTo(destination);
        Position += count;
        return count;
    }

    public void Seek(long frame) => Position = Math.Clamp(frame, 0, samples.Length);

    public void Rewind(long frame) => Seek(frame);

    /// <summary>Bumped by the test like a mute; <c>Rewinds</c> counts the read-ahead drops it caused.</summary>
    public int ChangeVersion { get; set; }
}

/// <summary>The WSOLA time stretcher: duration, pitch, continuity, passthrough, seek, speed changes, allocation. Pure C#, runs on any OS.</summary>
public sealed class WsolaStretcherTests
{
    private const int Rate = 16_000;

    internal static short[] Sine(double hertz, int frames, double amplitude = 10_000, double phase = 0)
    {
        var samples = new short[frames];
        for (var i = 0; i < frames; i++)
        {
            samples[i] = (short)Math.Round(amplitude * Math.Sin(phase + (2 * Math.PI * hertz * i / Rate)));
        }

        return samples;
    }

    /// <summary>A voiced-speech stand-in: a 120 Hz fundamental with a few harmonics and a slow amplitude wobble.</summary>
    internal static short[] Voice(int frames)
    {
        var samples = new short[frames];
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / Rate;
            var sum = Math.Sin(2 * Math.PI * 120 * t) + (0.6 * Math.Sin(2 * Math.PI * 240 * t)) + (0.4 * Math.Sin(2 * Math.PI * 360 * t)) + (0.2 * Math.Sin(2 * Math.PI * 600 * t));
            samples[i] = (short)Math.Round(6_000 * sum * (0.7 + (0.3 * Math.Sin(2 * Math.PI * 3 * t))));
        }

        return samples;
    }

    private static short[] RenderAll(WsolaStretcher stretcher, int chunk = 800)
    {
        var all = new List<short>();
        var buffer = new short[chunk];
        int written;
        while ((written = stretcher.Render(buffer)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, written).ToArray());
        }

        return [.. all];
    }

    private static WsolaStretcher Create(short[] input, double speed)
    {
        var stretcher = new WsolaStretcher(new ArraySource(input), Rate);
        stretcher.SetSpeed(speed);
        return stretcher;
    }

    /// <summary>Frequency by upward zero crossings (interpolated), ignoring the first and last 100 ms.</summary>
    private static double Frequency(ReadOnlySpan<short> samples)
    {
        var from = Rate / 10;
        var to = samples.Length - (Rate / 10);
        double first = -1, last = -1;
        var crossings = 0;
        for (var i = from; i < to; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0)
            {
                var at = i - 1 + (-(double)samples[i - 1] / (samples[i] - samples[i - 1]));
                if (first < 0)
                {
                    first = at;
                }

                last = at;
                crossings++;
            }
        }

        return (crossings - 1) * Rate / (last - first);
    }

    private static int MaxStep(ReadOnlySpan<short> samples, int skip = 0)
    {
        var max = 0;
        for (var i = skip + 1; i < samples.Length; i++)
        {
            max = Math.Max(max, Math.Abs(samples[i] - samples[i - 1]));
        }

        return max;
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void Duration_ScalesByTheInverseOfTheSpeed(double speed)
    {
        var input = Voice(5 * Rate + 123);

        var output = RenderAll(Create(input, speed));

        var expected = Math.Ceiling(input.Length / speed);
        Assert.InRange(output.Length, expected - 1, expected + 1); // well within the "a few ms" budget (1 frame = 62 us)
    }

    [Fact]
    public void Speed1_IsABitExactPassthrough_InAnyChunking()
    {
        var input = Voice(3 * Rate + 77);
        foreach (var chunk in new[] { 1_600, 800, 333, 1 + 160 })
        {
            var output = RenderAll(Create(input, 1), chunk);

            Assert.Equal(input, output);
        }
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void ASineKeepsItsFrequency(double speed)
    {
        foreach (var hertz in new[] { 200.0, 440.0, 1_000.0 })
        {
            var output = RenderAll(Create(Sine(hertz, 6 * Rate), speed));

            var measured = Frequency(output);

            Assert.InRange(measured, hertz * 0.99, hertz * 1.01);
        }
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void AVoicedSignalKeepsItsFundamental(double speed)
    {
        var output = RenderAll(Create(Voice(8 * Rate), speed));

        // The sum of harmonics of 120 Hz crosses zero at the fundamental's rate (the 240/360 Hz parts add a few extra crossings).
        var reference = Frequency(RenderAll(Create(Voice(8 * Rate), 1)));
        Assert.InRange(Frequency(output), reference * 0.97, reference * 1.03);
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void NoDiscontinuityAtTheJoinsOfASine(double speed)
    {
        const double amplitude = 10_000;
        foreach (var hertz in new[] { 150.0, 440.0, 1_000.0 })
        {
            var input = Sine(hertz, 4 * Rate, amplitude);
            var naturalStep = amplitude * 2 * Math.PI * hertz / Rate;

            var output = RenderAll(Create(input, speed), chunk: 777);

            Assert.True(MaxStep(output, skip: 80) <= (naturalStep * 1.1) + 2, $"{hertz} Hz at {speed}x: step {MaxStep(output, 80)} vs natural {naturalStep:F0}");
        }
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void NoClickAtTheJoinsOfAVoicedSignal(double speed)
    {
        var input = Voice(6 * Rate);

        var output = RenderAll(Create(input, speed));

        // No jump beyond a small multiple of the steepest slope of the input itself.
        Assert.True(MaxStep(output, skip: 80) <= MaxStep(input) * 1.5, $"step {MaxStep(output, 80)} vs input {MaxStep(input)}");
    }

    [Fact]
    public void MediaPosition_AdvancesBySpeedPerOutputFrame_AndEndsAtTheTotal()
    {
        var input = Voice(4 * Rate);
        var stretcher = Create(input, 1.5);
        var buffer = new short[Rate];

        var first = stretcher.Render(buffer);

        Assert.Equal(Rate, first);
        Assert.Equal(Rate * 1.5, stretcher.MediaPosition, 1e-6);
        Assert.Equal(1.5, stretcher.OutputSpeed);
        while (stretcher.Render(buffer) > 0)
        {
        }

        Assert.True(stretcher.AtEnd);
        Assert.InRange(stretcher.MediaPosition, input.Length, input.Length + 2);
    }

    [Fact]
    public void Seek_ResetsTheStateAndFadesIn()
    {
        var input = Sine(440, 6 * Rate, 10_000);
        var stretcher = Create(input, 1.5);
        var buffer = new short[4_000];
        stretcher.Render(buffer);

        stretcher.Seek(3 * Rate);

        Assert.Equal(3 * Rate, stretcher.MediaPosition);
        Assert.False(stretcher.AtEnd);
        var after = new short[Rate];
        Assert.Equal(Rate, stretcher.Render(after));
        Assert.True(Math.Abs(after[0]) < 2_500, $"first frame after a seek must start from silence, was {after[0]}"); // 1/65 of the content
        Assert.True(MaxStep(after, 0) <= 1_728 * 1.1 + 2);
        Assert.Equal(3 * Rate + (Rate * 1.5), stretcher.MediaPosition, 1e-6);

        // The rest of the stream is as long as the remaining media says.
        var rest = RenderAll(stretcher).Length + Rate;
        Assert.InRange(rest, Math.Ceiling(3 * Rate / 1.5) - 1, Math.Ceiling(3 * Rate / 1.5) + 1);
    }

    [Fact]
    public void SeekAtSpeed1_StaysBitExact()
    {
        var input = Voice(3 * Rate);
        var stretcher = Create(input, 1);
        stretcher.Render(new short[5_000]);

        stretcher.Seek(10_000);

        Assert.Equal(input.AsSpan(10_000).ToArray(), RenderAll(stretcher));
    }

    [Fact]
    public void ChangingTheSpeedWhilePlaying_IsSeamless_AndTakesEffectWithin100Ms()
    {
        const double amplitude = 10_000;
        var input = Sine(300, 12 * Rate, amplitude);
        var naturalStep = amplitude * 2 * Math.PI * 300 / Rate;
        var stretcher = Create(input, 1);
        var output = new List<short>();
        var buffer = new short[800];
        var plan = new[] { 1.25, 1.0, 2.0, 0.75, 1.0, 1.5 };

        foreach (var speed in plan)
        {
            Assert.Equal(speed, stretcher.SetSpeed(speed));
            var rendered = 0;
            var effectiveAt = -1;
            for (var block = 0; block < 5; block++) // 250 ms of output per speed
            {
                var written = stretcher.Render(buffer);
                output.AddRange(buffer.AsSpan(0, written).ToArray());
                rendered += written;
                if (effectiveAt < 0 && stretcher.OutputSpeed == speed)
                {
                    effectiveAt = rendered;
                }
            }

            Assert.True(effectiveAt is >= 0 and <= 1_600, $"speed {speed} reached the output after {effectiveAt} frames");
        }

        output.AddRange(RenderAll(stretcher));
        var all = output.ToArray();
        Assert.True(MaxStep(all, 80) <= (naturalStep * 1.1) + 2, $"step {MaxStep(all, 80)} vs natural {naturalStep:F0}");
        Assert.True(stretcher.AtEnd);
        Assert.InRange(stretcher.MediaPosition, input.Length, input.Length + 2); // the last output frame may overshoot by < speed
    }

    [Fact]
    public void ReturningToSpeed1_ContinuesWhereTheStretchedAudioWas_WithoutRepeatsOrGaps()
    {
        // A ramp makes any repeated or skipped input frame visible as a step in the derivative.
        var input = new short[4 * Rate];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (short)((i % 20_000) - 10_000);
        }

        var stretcher = Create(input, 1.5);
        stretcher.Render(new short[3_200]);
        stretcher.SetSpeed(1);
        var buffer = new short[8_000];
        var written = stretcher.Render(buffer);

        // Pass-through starts with the hop being played (a stretched, cross-faded one), then the source itself: from some point on
        // the output is the input shifted by a constant.
        Assert.Equal(8_000, written);
        var tail = buffer.AsSpan(3_000, 4_000);
        var shift = input.AsSpan().IndexOf(tail[..64]);
        Assert.True(shift >= 0);
        Assert.Equal(input.AsSpan(shift, 4_000).ToArray(), tail.ToArray());
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(0.1, 0.5)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.75, 0.75)]
    [InlineData(2, 2)]
    [InlineData(9, 2)]
    [InlineData(1.003, 1)]
    [InlineData(0.998, 1)]
    [InlineData(-3, 0.5)]
    public void SetSpeed_Clamps_AndReportsTheSpeedInEffect(double asked, double expected)
    {
        var stretcher = Create(new short[100], 1);

        Assert.Equal(expected, stretcher.SetSpeed(asked));
        Assert.Equal(expected, stretcher.Speed);
        Assert.Equal(expected, WsolaStretcher.ClampSpeed(asked));
    }

    [Fact]
    public void ASourceShorterThanAHop_And_AnEmptyOne_Work()
    {
        foreach (var length in new[] { 0, 1, 50, 319, 320, 321, 500 })
        {
            foreach (var speed in new[] { 0.75, 2 })
            {
                var output = RenderAll(Create(Voice(length), speed));

                Assert.InRange(output.Length, Math.Ceiling(length / speed) - 1, Math.Ceiling(length / speed) + 1);
            }
        }
    }

    [Fact]
    public void Silence_StaysSilent()
    {
        var output = RenderAll(Create(new short[3 * Rate], 1.5));

        Assert.All(output, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Render_AtSpeedOtherThan1_DoesNotAllocate()
    {
        var input = Voice(4 * Rate);
        var source = new ArraySource(input);
        var stretcher = new WsolaStretcher(source, Rate);
        var buffer = new short[800];

        // Warm up (JIT, tiering) through every path: stretched, speed changes, seek, end, passthrough.
        for (var pass = 0; pass < 3; pass++)
        {
            Exercise(stretcher, buffer);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        Exercise(stretcher, buffer);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void Exercise(WsolaStretcher stretcher, short[] buffer)
    {
        stretcher.Seek(0);
        stretcher.SetSpeed(1.5);
        for (var i = 0; i < 20; i++)
        {
            stretcher.Render(buffer);
        }

        stretcher.SetSpeed(0.75);
        for (var i = 0; i < 20; i++)
        {
            stretcher.Render(buffer);
        }

        stretcher.SetSpeed(1);
        stretcher.Render(buffer);
        stretcher.SetSpeed(2);
        stretcher.Seek(30_000);
        while (stretcher.Render(buffer) > 0)
        {
        }
    }
}
