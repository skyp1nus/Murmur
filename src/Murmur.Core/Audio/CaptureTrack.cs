using System.Numerics;
using System.Runtime.InteropServices;
using Murmur.Core.Capture;

namespace Murmur.Core.Audio;

/// <summary>
/// The <see cref="IAudioSink"/> of one audio source: buffers the source's interleaved audio in a lock-free ring for the
/// recorder's writer thread, tracks the peak level for the recording orb and counts written and dropped frames.
/// </summary>
/// <remarks>
/// <see cref="Write"/> and <see cref="WriteSilence"/> run on the capture thread (single producer): no allocations,
/// no locks, no logging. <see cref="Read"/> runs on the writer thread (single consumer). Only whole frames are ever
/// stored, so a reader that reads whole frames stays channel-aligned.
/// </remarks>
public sealed class CaptureTrack : IAudioSink
{
    /// <summary>Minimum ring length in seconds of source audio.</summary>
    public const double MinimumRingSeconds = 4;

    private readonly AudioRingBuffer _ring;
    private readonly int _channels;
    private long _framesWritten;
    private long _framesDropped;
    private int _lastPeakBits;
    private int _heldPeakBits;
    private volatile bool _faulted;

    /// <param name="format">Native format of the source.</param>
    /// <param name="ringSeconds">Ring length in seconds of source audio; at least <see cref="MinimumRingSeconds"/>.</param>
    public CaptureTrack(AudioFormat format, double ringSeconds = MinimumRingSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(format.SampleRate, 1, nameof(format));
        ArgumentOutOfRangeException.ThrowIfLessThan(format.Channels, 1, nameof(format));
        if (!(ringSeconds >= MinimumRingSeconds) || double.IsInfinity(ringSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(ringSeconds), ringSeconds, $"Must be at least {MinimumRingSeconds} s.");
        }

        Format = format;
        _channels = format.Channels;
        var samples = Math.Ceiling(ringSeconds * format.SampleRate) * format.Channels;
        if (samples > 1 << 30)
        {
            throw new ArgumentOutOfRangeException(nameof(ringSeconds), ringSeconds, "Ring would be larger than 2^30 samples.");
        }

        _ring = new AudioRingBuffer((int)samples);
    }

    public AudioFormat Format { get; }

    /// <summary>Ring capacity in frames.</summary>
    public int CapacityFrames => _ring.Capacity / _channels;

    /// <summary>Frames waiting to be read.</summary>
    public int AvailableFrames => _ring.Available / _channels;

    /// <summary>Frames accepted from the source (audio and silence).</summary>
    public long FramesWritten => Volatile.Read(ref _framesWritten);

    /// <summary>Frames lost because the ring was full. Must stay 0.</summary>
    public long FramesDropped => Volatile.Read(ref _framesDropped);

    /// <summary>Peak (max |x|) of the most recent buffer, in [0, +inf).</summary>
    public float LastPeak => BitConverter.Int32BitsToSingle(Volatile.Read(ref _lastPeakBits));

    /// <summary>Set by the owner when the source reported a fatal fault.</summary>
    public bool IsFaulted => _faulted;

    public void MarkFaulted() => _faulted = true;

    /// <inheritdoc />
    public void Write(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / _channels;
        var fit = Math.Min(frames, _ring.FreeSpace / _channels);
        var peak = Peak(interleaved[..(frames * _channels)]);

        if (fit > 0)
        {
            _ring.Write(interleaved[..(fit * _channels)]);
            Volatile.Write(ref _framesWritten, _framesWritten + fit);
        }

        if (fit < frames)
        {
            Volatile.Write(ref _framesDropped, _framesDropped + (frames - fit));
        }

        PublishPeak(peak);
    }

    /// <inheritdoc />
    public void WriteSilence(int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        var fit = Math.Min(frames, _ring.FreeSpace / _channels);
        if (fit > 0)
        {
            _ring.WriteZeros(fit * _channels);
            Volatile.Write(ref _framesWritten, _framesWritten + fit);
        }

        if (fit < frames)
        {
            Volatile.Write(ref _framesDropped, _framesDropped + (frames - fit));
        }

        Volatile.Write(ref _lastPeakBits, 0);
    }

    /// <summary>Consumer only. Reads whole frames into <paramref name="destination"/>.</summary>
    /// <returns>Number of frames read.</returns>
    public int Read(Span<float> destination)
    {
        var maxSamples = destination.Length / _channels * _channels;
        return _ring.Read(destination[..maxSamples]) / _channels;
    }

    /// <summary>Returns the highest peak since the previous call and resets it. Safe from any single polling thread.</summary>
    public float TakePeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _heldPeakBits, 0));

    private void PublishPeak(float peak)
    {
        var bits = BitConverter.SingleToInt32Bits(peak);
        Volatile.Write(ref _lastPeakBits, bits);

        // Non-negative floats order like their bit patterns. A racing TakePeak may lose one buffer's peak; harmless for a meter.
        if (bits > Volatile.Read(ref _heldPeakBits))
        {
            Volatile.Write(ref _heldPeakBits, bits);
        }
    }

    /// <summary>Max |x| of <paramref name="samples"/>, vectorized. A NaN result is reported as 0.</summary>
    internal static float Peak(ReadOnlySpan<float> samples)
    {
        var i = 0;
        var max = 0f;
        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            var vectors = MemoryMarshal.Cast<float, Vector<float>>(samples);
            foreach (var v in vectors)
            {
                acc = Vector.Max(acc, Vector.Abs(v));
            }

            for (var lane = 0; lane < Vector<float>.Count; lane++)
            {
                max = MathF.Max(max, acc[lane]);
            }

            i = vectors.Length * Vector<float>.Count;
        }

        for (; i < samples.Length; i++)
        {
            var a = MathF.Abs(samples[i]);
            if (a > max)
            {
                max = a;
            }
        }

        return float.IsNaN(max) ? 0f : max;
    }
}
