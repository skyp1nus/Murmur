using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;
using Murmur.Capture.Windows.Video;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// A short H.264 MP4 whose colour encodes the time: <see cref="SlotDuration"/> slots of solid <see cref="Palette"/>
/// colours, 30 fps, written by the recorder's own <see cref="H264SinkWriter"/> (CPU input, Microsoft software encoder),
/// so it needs no desktop and no GPU. Windows only.
/// </summary>
internal static unsafe class TestVideo
{
    public const int Width = 320;
    public const int Height = 240;
    public const int FrameRate = 30;

    public static readonly TimeSpan SlotDuration = TimeSpan.FromMilliseconds(500);

    /// <summary>(R, G, B) per slot: far apart, so H.264 / YUV rounding never makes one look like another.</summary>
    public static readonly (byte R, byte G, byte B)[] Palette =
    [
        (255, 0, 0),
        (0, 255, 0),
        (0, 0, 255),
        (255, 255, 255),
        (0, 0, 0),
        (255, 255, 0),
        (0, 255, 255),
        (255, 0, 255),
    ];

    public static TimeSpan Duration => SlotDuration * Palette.Length;

    /// <summary>The palette slot shown at <paramref name="time"/>.</summary>
    public static int SlotAt(TimeSpan time) => Math.Clamp((int)(time.Ticks / SlotDuration.Ticks), 0, Palette.Length - 1);

    /// <summary>Why the video tests cannot run here, or null.</summary>
    public static string? SkipReason()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Video playback tests run only on Windows.";
        }

        var playback = new WindowsVideoPlayback();
        return playback.UnsupportedReason;
    }

    /// <summary>Writes the test video to <paramref name="path"/>; skips the test when no H.264 encoder is available.</summary>
    public static void Write(string path)
    {
        MediaFoundationRuntime.EnsureStarted();
        var size = new PixelSize(Width, Height);
        var settings = FramePathPolicy.Settings(EncoderInput.Cpu, new H264EncoderSettings(size, FrameRate, 2_000));
        H264SinkWriter writer;
        try
        {
            writer = H264SinkWriter.Create(path, settings, EncoderInput.Cpu, 0);
        }
        catch (COMException ex)
        {
            Assert.Skip($"No H.264 encoder to write the test video: {ex.Message}");
            throw;
        }

        using (writer)
        {
            var stride = CpuFrameCopier.Stride(size);
            var length = (uint)(stride * Height);
            var frames = (int)(Duration.Ticks * FrameRate / TimeSpan.TicksPerSecond);
            var frameTicks = TimeSpan.TicksPerSecond / FrameRate;
            for (var i = 0; i < frames; i++)
            {
                var time = i * frameTicks;
                Assert.Equal(0, MediaFoundationSampleFactory.Instance.Create(length, out var sample, out var buffer));
                try
                {
                    Fill(buffer, length, Palette[SlotAt(TimeSpan.FromTicks(time))]);
                    Assert.Equal(0, IMFSample.SetSampleTime(sample, time));
                    Assert.Equal(0, IMFSample.SetSampleDuration(sample, frameTicks));
                    Assert.Equal(0, writer.WriteSample(sample, time));
                }
                finally
                {
                    ComCall.Release(ref buffer);
                    ComCall.Release(ref sample);
                }
            }

            Assert.Equal(0, writer.FinalizeFile());
        }
    }

    /// <summary>The palette slot nearest to the BGRA pixel at the centre of a frame, and its distance.</summary>
    public static (int Slot, double Distance) CentreSlot(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        var offset = ((height / 2) * stride) + ((width / 2) * 4);
        var (b, g, r) = (bgra[offset], bgra[offset + 1], bgra[offset + 2]);
        var best = (Slot: -1, Distance: double.MaxValue);
        for (var i = 0; i < Palette.Length; i++)
        {
            var (pr, pg, pb) = Palette[i];
            var distance = Math.Sqrt(Math.Pow(r - pr, 2) + Math.Pow(g - pg, 2) + Math.Pow(b - pb, 2));
            if (distance < best.Distance)
            {
                best = (i, distance);
            }
        }

        return best;
    }

    private static void Fill(nint buffer, uint length, (byte R, byte G, byte B) colour)
    {
        byte* data;
        uint max, current;
        Assert.Equal(0, IMFMediaBuffer.Lock(buffer, &data, &max, &current));
        try
        {
            var pixels = new Span<uint>(data, (int)(length / 4));
            pixels.Fill(0xFF00_0000u | ((uint)colour.R << 16) | ((uint)colour.G << 8) | colour.B); // B, G, R, X bytes
        }
        finally
        {
            IMFMediaBuffer.Unlock(buffer);
        }

        Assert.Equal(0, IMFMediaBuffer.SetCurrentLength(buffer, length));
    }
}
