using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Audio;

public sealed class CaptureTrackTests
{
    private static readonly AudioFormat Stereo48k = new(48_000, 2);

    [Fact]
    public void Ring_HoldsAtLeastFourSecondsOfSourceAudio()
    {
        var track = new CaptureTrack(Stereo48k);

        Assert.True(track.CapacityFrames >= 4 * 48_000, $"{track.CapacityFrames} frames");
    }

    [Fact]
    public void RingSeconds_BelowFourIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureTrack(Stereo48k, 1));
    }

    [Fact]
    public void Write_CountsFramesAndPublishesThePeak()
    {
        var track = new CaptureTrack(Stereo48k);

        track.Write([0.1f, -0.2f, 0.3f, -0.75f]);

        Assert.Equal(2, track.FramesWritten);
        Assert.Equal(2, track.AvailableFrames);
        Assert.Equal(0.75f, track.LastPeak);
        Assert.Equal(0.75f, track.TakePeak());
        Assert.Equal(0f, track.TakePeak());
    }

    [Fact]
    public void TakePeak_HoldsTheHighestPeakSinceTheLastCall()
    {
        var track = new CaptureTrack(Stereo48k);

        track.Write([0.9f, 0f]);
        track.Write([0.1f, 0f]);

        Assert.Equal(0.1f, track.LastPeak);
        Assert.Equal(0.9f, track.TakePeak());
    }

    [Fact]
    public void Peak_IsVectorizedAndIgnoresSign()
    {
        var samples = new float[1001];
        samples[777] = -0.6f;
        samples[1000] = 0.5f;

        Assert.Equal(0.6f, CaptureTrack.Peak(samples));
        Assert.Equal(0f, CaptureTrack.Peak([]));
    }

    [Fact]
    public void WriteSilence_StoresZeroFrames()
    {
        var track = new CaptureTrack(Stereo48k);
        track.Write([0.5f, 0.5f]);

        track.WriteSilence(3);

        Assert.Equal(4, track.FramesWritten);
        Assert.Equal(0f, track.LastPeak);
        var read = new float[16];
        Assert.Equal(4, track.Read(read));
        Assert.Equal([0.5f, 0.5f, 0, 0, 0, 0, 0, 0], read[..8]);
    }

    [Fact]
    public void Read_ReturnsWholeFramesOnly()
    {
        var track = new CaptureTrack(Stereo48k);
        track.Write([1, 2, 3, 4, 5, 6]);

        var odd = new float[3];
        Assert.Equal(1, track.Read(odd));
        Assert.Equal([1f, 2f], odd[..2]);
        Assert.Equal(2, track.AvailableFrames);
    }

    [Fact]
    public void Overflow_DropsWholeFramesAndCountsThem()
    {
        var track = new CaptureTrack(new AudioFormat(1_000, 2)); // 4 s = 8000 samples -> ring of 8192 samples = 4096 frames
        var capacity = track.CapacityFrames;

        track.Write(new float[(capacity - 1) * 2]);
        track.Write(new float[3 * 2]);
        track.WriteSilence(5);

        Assert.Equal(capacity, track.FramesWritten);
        Assert.Equal(2 + 5, track.FramesDropped);
        Assert.Equal(capacity, track.AvailableFrames);
    }

    [Fact]
    public void MarkFaulted_IsVisible()
    {
        var track = new CaptureTrack(Stereo48k);
        Assert.False(track.IsFaulted);

        track.MarkFaulted();

        Assert.True(track.IsFaulted);
    }

    [Fact]
    public void WriteAndWriteSilence_DoNotAllocate()
    {
        var track = new CaptureTrack(Stereo48k);
        var chunk = new float[960];
        var read = new float[4096];
        for (var i = 0; i < chunk.Length; i++)
        {
            chunk[i] = MathF.Sin(i * 0.01f);
        }

        track.Write(chunk);
        track.Read(read);

        var allocated = Allocations.SteadyState(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                track.Write(chunk);
                track.WriteSilence(480);
                track.Read(read);
                track.TakePeak();
            }
        });
        Assert.Equal(0, allocated);
        Assert.Equal(0, track.FramesDropped);
    }
}
