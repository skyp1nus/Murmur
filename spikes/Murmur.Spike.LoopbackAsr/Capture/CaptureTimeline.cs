using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Murmur.Spike.LoopbackAsr.Capture;

internal enum PacketSampleFormat
{
    Float32,
    Pcm16,
}

/// <summary>
/// Platform-independent part of the loopback capture: converts device packets to mono float and places them
/// on a wall-clock timeline. WASAPI loopback delivers no packets while nothing is playing, so gaps are filled
/// with silence using the packet QPC timestamps; the result is exactly <c>seconds * rate</c> frames.
/// All buffers are preallocated: <see cref="Append"/> does not allocate.
/// </summary>
internal sealed class CaptureTimeline
{
    private readonly float[] _mono;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly PacketSampleFormat _format;
    private readonly long _startTime100ns;
    private readonly int _gapThresholdFrames;
    private int _written;

    public CaptureTimeline(int sampleRate, int channels, PacketSampleFormat format, double seconds, long startTime100ns)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _format = format;
        _startTime100ns = startTime100ns;
        _mono = new float[(int)Math.Ceiling(seconds * sampleRate)];
        // Packets arrive every ~10 ms; treat a hole larger than 30 ms as a real gap (nothing was playing).
        _gapThresholdFrames = sampleRate * 30 / 1000;
    }

    public int SampleRate => _sampleRate;

    public int WrittenFrames => _written;

    public int CapacityFrames => _mono.Length;

    public bool IsFull => _written >= _mono.Length;

    /// <summary>Number of silence frames inserted to cover gaps (diagnostics).</summary>
    public long GapFrames { get; private set; }

    /// <summary>Number of packets flagged AUDCLNT_BUFFERFLAGS_SILENT.</summary>
    public int SilentPackets { get; private set; }

    /// <summary>Number of packets flagged AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY.</summary>
    public int Discontinuities { get; private set; }

    public int BytesPerFrame => _channels * (_format == PacketSampleFormat.Float32 ? 4 : 2);

    /// <summary>
    /// Appends one device packet. <paramref name="qpc100ns"/> is the packet's QPC position in 100 ns units
    /// (0 = unknown). <paramref name="data"/> is ignored when <paramref name="silent"/> is set.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data, int frames, bool silent, bool discontinuity, long qpc100ns)
    {
        if (discontinuity)
        {
            Discontinuities++;
        }

        if (qpc100ns > 0)
        {
            var target = (qpc100ns - _startTime100ns) * _sampleRate / 10_000_000;
            if (target - _written > _gapThresholdFrames)
            {
                FillSilenceTo((int)Math.Min(target, _mono.Length));
            }
        }

        var free = _mono.Length - _written;
        if (frames > free)
        {
            frames = free;
        }

        if (frames <= 0)
        {
            return;
        }

        var dst = _mono.AsSpan(_written, frames);
        if (silent)
        {
            SilentPackets++;
            dst.Clear();
        }
        else
        {
            ConvertToMono(data[..(frames * BytesPerFrame)], _format, _channels, dst);
        }

        _written += frames;
    }

    /// <summary>Pads with silence up to <paramref name="frame"/> (clamped to capacity).</summary>
    public void FillSilenceTo(int frame)
    {
        frame = Math.Min(frame, _mono.Length);
        if (frame <= _written)
        {
            return;
        }

        _mono.AsSpan(_written, frame - _written).Clear();
        GapFrames += frame - _written;
        _written = frame;
    }

    /// <summary>Pads the tail with silence so the result covers the full requested duration.</summary>
    public float[] Complete()
    {
        FillSilenceTo(_mono.Length);
        return _mono;
    }

    public static void ConvertToMono(ReadOnlySpan<byte> data, PacketSampleFormat format, int channels, Span<float> mono)
    {
        var frames = mono.Length;
        var scale = 1f / channels;
        if (format == PacketSampleFormat.Float32)
        {
            var src = MemoryMarshal.Cast<byte, float>(data);
            for (var f = 0; f < frames; f++)
            {
                var sum = 0f;
                var row = src.Slice(f * channels, channels);
                for (var c = 0; c < row.Length; c++)
                {
                    sum += row[c];
                }

                mono[f] = sum * scale;
            }
        }
        else
        {
            for (var f = 0; f < frames; f++)
            {
                var sum = 0;
                var row = data.Slice(f * channels * 2, channels * 2);
                for (var c = 0; c < channels; c++)
                {
                    sum += BinaryPrimitives.ReadInt16LittleEndian(row[(c * 2)..]);
                }

                mono[f] = sum * scale / 32768f;
            }
        }
    }
}
