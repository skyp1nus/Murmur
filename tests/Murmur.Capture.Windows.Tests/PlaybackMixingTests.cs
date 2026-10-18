using Murmur.Capture.Windows.Audio;
using Murmur.Capture.Windows.Playback;

namespace Murmur.Capture.Windows.Tests;

public sealed class SoftClipTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(12_345)]
    [InlineData(PlaybackMixer.Knee)]
    [InlineData(-PlaybackMixer.Knee)]
    public void BelowKnee_IsIdentity(int sum) => Assert.Equal(sum, PlaybackMixer.SoftClip(sum));

    [Fact]
    public void AboveKnee_IsMonotonic_Bounded_AndOddSymmetric()
    {
        var previous = (int)PlaybackMixer.SoftClip(PlaybackMixer.Knee);
        for (var sum = PlaybackMixer.Knee + 1; sum <= 2 * 32_768; sum++)
        {
            int clipped = PlaybackMixer.SoftClip(sum);
            Assert.InRange(clipped, previous, short.MaxValue);
            Assert.True(clipped <= sum);
            Assert.Equal(-clipped, PlaybackMixer.SoftClip(-sum));
            previous = clipped;
        }

        Assert.True(previous > 32_000, $"Full-scale sum should approach full scale, got {previous}.");
        Assert.InRange((int)PlaybackMixer.SoftClip(-65_536), -short.MaxValue, -32_700); // the most negative 2-track sum
    }

    [Fact]
    public void IsContinuousAtTheKnee()
    {
        // Slope 1 at the knee: one step above it moves the output by at most one LSB.
        Assert.InRange(PlaybackMixer.SoftClip(PlaybackMixer.Knee + 1) - PlaybackMixer.Knee, 0, 1);
        Assert.InRange(PlaybackMixer.SoftClip(PlaybackMixer.Knee + 100) - PlaybackMixer.Knee, 98, 100);
    }

    [Fact]
    public void AccumulateAndClip_SumSampleWise()
    {
        short[] a = [100, -200, 30_000, -30_000, 5];
        short[] b = [1, 2, 30_000, -30_000];
        var acc = new int[a.Length];
        var output = new short[a.Length];

        PlaybackMixer.Accumulate(a, acc);
        PlaybackMixer.Accumulate(b, acc);
        PlaybackMixer.Clip(acc, output);

        Assert.Equal([101, -198, 60_000, -60_000, 5], acc);
        Assert.Equal(101, output[0]);
        Assert.Equal(-198, output[1]);
        Assert.InRange((int)output[2], 32_000, short.MaxValue);
        Assert.Equal(-output[2], output[3]);
        Assert.Equal(5, output[4]);
    }
}

public sealed class Pcm16TrackReaderTests
{
    [Fact]
    public void Read_InChunks_ReturnsTheSamplesInOrder()
    {
        var samples = TestWav.Ramp(10_000, -5_000);
        using var reader = TestWav.Reader(samples);
        Assert.Equal(16_000, reader.SampleRate);
        Assert.Equal(10_000, reader.FrameCount);

        var all = new List<short>();
        var chunk = new short[777];
        int read;
        while ((read = reader.Read(chunk)) > 0)
        {
            all.AddRange(chunk.AsSpan(0, read).ToArray());
            Assert.Equal(all.Count, reader.Position);
        }

        Assert.Equal(samples, all);
        Assert.Equal(0, reader.Read(chunk));
    }

    [Fact]
    public void Seek_Repositions_AndClamps()
    {
        var samples = TestWav.Ramp(1_000);
        using var reader = TestWav.Reader(samples);
        var buffer = new short[4];

        reader.Seek(500);
        Assert.Equal(4, reader.Read(buffer));
        Assert.Equal([500, 501, 502, 503], buffer);

        reader.Seek(-10);
        Assert.Equal(0, reader.Position);
        Assert.Equal(4, reader.Read(buffer));
        Assert.Equal([0, 1, 2, 3], buffer);

        reader.Seek(5_000);
        Assert.Equal(1_000, reader.Position);
        Assert.Equal(0, reader.Read(buffer));

        reader.Seek(998);
        Assert.Equal(2, reader.Read(buffer));
        Assert.Equal([998, 999], buffer.AsSpan(0, 2).ToArray());
    }

    [Fact]
    public void StaleHeader_UsesWhatIsPhysicallyPresent()
    {
        var samples = TestWav.Ramp(300);
        using (var zero = Pcm16TrackReader.Open(new MemoryStream(TestWav.Pcm16(samples, declaredDataBytes: 0))))
        {
            Assert.Equal(300, zero.FrameCount); // 0 = "to the end" (crashed writer)
        }

        using (var tooLarge = Pcm16TrackReader.Open(new MemoryStream(TestWav.Pcm16(samples, declaredDataBytes: 1_000_000))))
        {
            Assert.Equal(300, tooLarge.FrameCount);
        }

        using var shorter = Pcm16TrackReader.Open(new MemoryStream(TestWav.Pcm16(samples, declaredDataBytes: 200)));
        Assert.Equal(100, shorter.FrameCount);
    }

    [Theory]
    [InlineData((ushort)1, 2, 16)] // stereo PCM16
    [InlineData((ushort)1, 1, 24)] // 24-bit
    [InlineData((ushort)1, 1, 8)]  // 8-bit
    [InlineData((ushort)3, 1, 32)] // float
    public void Open_RejectsAnythingButMonoPcm16(ushort tag, int channels, int bits)
    {
        var bytes = TestWav.Raw(tag, channels, 16_000, bits, 10);
        Assert.Throws<InvalidDataException>(() => Pcm16TrackReader.Open(new MemoryStream(bytes)));
    }

    [Fact]
    public void Open_RejectsNonWav_AndMissingFiles()
    {
        Assert.Throws<InvalidDataException>(() => Pcm16TrackReader.Open(new MemoryStream(new byte[100])));
        Assert.Throws<FileNotFoundException>(() => Pcm16TrackReader.Open(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav")));
    }

    [Fact]
    public void Open_FromFile_ReadsLikeTheStream()
    {
        using var dir = new TempDir();
        var path = dir.Write("mic.wav", TestWav.Pcm16(TestWav.Ramp(100)));
        using var reader = Pcm16TrackReader.Open(path);
        var buffer = new short[100];
        Assert.Equal(100, reader.Read(buffer));
        Assert.Equal(TestWav.Ramp(100), buffer);
    }
}

public sealed class TrackMixerTests
{
    [Fact]
    public void TwoTracks_AreSummed_ShorterOnePaddedWithSilence()
    {
        var mic = TestWav.Ramp(1_000, 0, 2);
        var system = TestWav.Constant(600, 7);
        using var mixer = new TrackMixer([TestWav.Reader(mic), TestWav.Reader(system)]);
        Assert.Equal(1_000, mixer.TotalFrames);

        var output = new short[1_200];
        var frames = mixer.Render(output);

        Assert.Equal(1_000, frames);
        Assert.True(mixer.AtEnd);
        for (var i = 0; i < frames; i++)
        {
            Assert.Equal(mic[i] + (i < 600 ? 7 : 0), output[i]);
        }

        Assert.Equal(0, mixer.Render(output));
    }

    [Fact]
    public void Sum_IsSoftClipped()
    {
        using var mixer = new TrackMixer([TestWav.Reader(TestWav.Constant(10, 30_000)), TestWav.Reader(TestWav.Constant(10, 30_000))]);
        var output = new short[10];
        mixer.Render(output);
        Assert.All(output, s => Assert.Equal(PlaybackMixer.SoftClip(60_000), s));
    }

    [Fact]
    public void SingleTrack_IsCopiedUnchanged()
    {
        var samples = TestWav.Constant(100, short.MaxValue);
        samples[1] = short.MinValue;
        using var mixer = new TrackMixer([TestWav.Reader(samples)]);
        var output = new short[100];
        Assert.Equal(100, mixer.Render(output));
        Assert.Equal(samples, output);
    }

    [Fact]
    public void LargeRequests_AreServedInPasses()
    {
        var total = (TrackMixer.MaxFramesPerRender * 3) + 17;
        var a = TestWav.Ramp(total, -10_000, 1);
        var b = TestWav.Constant(total, 3);
        using var mixer = new TrackMixer([TestWav.Reader(a), TestWav.Reader(b)]);
        var output = new short[total + 100];

        Assert.Equal(total, mixer.Render(output));
        for (var i = 0; i < total; i++)
        {
            Assert.Equal(a[i] + 3, output[i]);
        }
    }

    [Fact]
    public void Seek_RepositionsEveryTrack_AndClamps()
    {
        using var mixer = new TrackMixer([TestWav.Reader(TestWav.Ramp(1_000)), TestWav.Reader(TestWav.Ramp(400, 0, 0))]);
        var output = new short[3];

        mixer.Seek(398);
        Assert.Equal(398, mixer.Position);
        Assert.Equal(3, mixer.Render(output));
        Assert.Equal([398, 399, 400], output);

        mixer.Seek(5_000);
        Assert.Equal(1_000, mixer.Position);
        Assert.True(mixer.AtEnd);

        mixer.Seek(-1);
        Assert.Equal(0, mixer.Position);
        Assert.False(mixer.AtEnd);
    }

    [Fact]
    public void DifferentSampleRates_AreRejected()
    {
        var a = TestWav.Reader(TestWav.Ramp(10), 16_000);
        var b = TestWav.Reader(TestWav.Ramp(10), 48_000);
        Assert.Throws<InvalidDataException>(() => new TrackMixer([a, b]));
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public void Render_DoesNotAllocate()
    {
        const int Total = 16_000 * 30;
        using var mixer = new TrackMixer([TestWav.Reader(TestWav.Ramp(Total, 0, 0)), TestWav.Reader(TestWav.Constant(Total, 1))]);
        var output = new short[800];
        mixer.Render(output); // warm up
        mixer.Seek(0);

        var before = GC.GetAllocatedBytesForCurrentThread();
        while (mixer.Render(output) > 0)
        {
        }

        mixer.Seek(Total / 2);
        mixer.Render(output);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

public sealed class PlaybackTimingTests
{
    [Fact]
    public void FrameTimeConversions_AreExactAt16kHz()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), PlaybackTiming.ToTime(16_000, 16_000));
        Assert.Equal(TimeSpan.FromTicks(625), PlaybackTiming.ToTime(1, 16_000));
        Assert.Equal(16_000, PlaybackTiming.ToFrame(TimeSpan.FromSeconds(1), 16_000, long.MaxValue));
        for (long frame = 0; frame < 50_000; frame += 997)
        {
            Assert.Equal(frame, PlaybackTiming.ToFrame(PlaybackTiming.ToTime(frame, 16_000), 16_000, long.MaxValue));
            Assert.Equal(frame, PlaybackTiming.ToFrame(PlaybackTiming.ToTime(frame, 44_100), 44_100, long.MaxValue));
        }
    }

    [Fact]
    public void ToFrame_ClampsSeekTargets()
    {
        Assert.Equal(0, PlaybackTiming.ToFrame(TimeSpan.FromSeconds(-3), 16_000, 1_000));
        Assert.Equal(1_000, PlaybackTiming.ToFrame(TimeSpan.FromHours(1), 16_000, 1_000));
        Assert.Equal(8, PlaybackTiming.ToFrame(TimeSpan.FromTicks(5_000), 16_000, 1_000));
        Assert.Equal(TimeSpan.Zero, PlaybackTiming.Clamp(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(5), PlaybackTiming.Clamp(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(2), PlaybackTiming.Clamp(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void PlayedFrame_IsSubmittedMinusPadding_Clamped()
    {
        Assert.Equal(16_000 + 4_000 - 800, PlaybackTiming.PlayedFrame(16_000, 4_000, 800, 100_000));
        Assert.Equal(16_000, PlaybackTiming.PlayedFrame(16_000, 0, 0, 100_000)); // right after a flush
        Assert.Equal(0, PlaybackTiming.PlayedFrame(0, 100, 800, 100_000));
        Assert.Equal(100_000, PlaybackTiming.PlayedFrame(99_000, 5_000, 0, 100_000));
    }

    [Fact]
    public void Pcm16MonoFormat_IsAValidWaveFormatEx()
    {
        var bytes = PlaybackTiming.Pcm16MonoFormat(16_000);

        Assert.Equal(WaveFormatParser.WaveFormatExSize, bytes.Length);
        var format = WaveFormatParser.Parse(bytes);
        Assert.Equal(SampleType.Pcm16, format.SampleType);
        Assert.Equal(1, format.Channels);
        Assert.Equal(16_000, format.SampleRate);
        Assert.Equal([0x01, 0x00, 0x01, 0x00, 0x80, 0x3E, 0x00, 0x00, 0x00, 0x7D, 0x00, 0x00, 0x02, 0x00, 0x10, 0x00, 0x00, 0x00], bytes);
    }
}
