using System.Buffers.Binary;
using Murmur.Capture.Windows.Audio;

namespace Murmur.Capture.Windows.Playback;

/// <summary>Frame/time conversions and the position arithmetic of the render engine.</summary>
internal static class PlaybackTiming
{
    /// <summary>Frames to time, exact for 16 kHz (625 ticks per frame), truncating otherwise.</summary>
    public static TimeSpan ToTime(long frames, int sampleRate) =>
        TimeSpan.FromTicks((long)((Int128)frames * TimeSpan.TicksPerSecond / sampleRate));

    /// <summary>Time to the nearest frame, clamped to [0, <paramref name="totalFrames"/>] (seek clamping).</summary>
    public static long ToFrame(TimeSpan position, int sampleRate, long totalFrames)
    {
        if (position <= TimeSpan.Zero)
        {
            return 0;
        }

        var frame = (long)(((Int128)position.Ticks * sampleRate + (TimeSpan.TicksPerSecond / 2)) / TimeSpan.TicksPerSecond);
        return Math.Min(frame, totalFrames);
    }

    /// <summary>Clamps a requested position to [0, <paramref name="duration"/>].</summary>
    public static TimeSpan Clamp(TimeSpan position, TimeSpan duration) =>
        position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position;

    /// <summary>
    /// The frame being heard: everything submitted since the last flush (counted from <paramref name="startFrame"/>)
    /// minus what still waits in the endpoint buffer (<c>GetCurrentPadding</c>), clamped to [0, total].
    /// </summary>
    public static long PlayedFrame(long startFrame, long submittedFrames, uint paddingFrames, long totalFrames) =>
        Math.Clamp(startFrame + submittedFrames - paddingFrames, 0, totalFrames);

    /// <summary>
    /// The media frame being heard when the output runs at <paramref name="speed"/> (media frames per output frame): the media
    /// position of everything submitted (<paramref name="submittedMedia"/>) minus what still waits in the endpoint buffer
    /// (<c>GetCurrentPadding</c> output frames, worth <c>padding * speed</c> media frames), clamped to
    /// [<paramref name="startFrame"/>, total]. At speed 1 this equals <see cref="PlayedFrame"/>.
    /// </summary>
    public static long PlayedMediaFrame(long startFrame, double submittedMedia, uint paddingFrames, double speed, long totalFrames)
    {
        var heard = Math.Floor(submittedMedia - (paddingFrames * speed) + 0.5);
        return Math.Clamp((long)Math.Max(heard, startFrame), startFrame, totalFrames);
    }

    /// <summary>WAVEFORMATEX (mmreg.h, 18 bytes, pack 1) for mono 16-bit PCM at <paramref name="sampleRate"/>.</summary>
    public static byte[] Pcm16MonoFormat(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var bytes = new byte[WaveFormatParser.WaveFormatExSize];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0), WaveFormatParser.WaveFormatPcm); // wFormatTag
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 1);                                // nChannels
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)sampleRate);                 // nSamplesPerSec
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)sampleRate * 2);             // nAvgBytesPerSec
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 2);                               // nBlockAlign
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 16);                              // wBitsPerSample
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 0);                               // cbSize
        return bytes;
    }
}
