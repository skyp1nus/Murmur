using System.Numerics;
using System.Runtime.InteropServices;

namespace Murmur.Core.Audio;

/// <summary>
/// Lock-free single-producer / single-consumer ring of float samples with a power-of-two capacity.
/// Exactly one thread may call the producer methods (<see cref="Write"/>, <see cref="WriteZeros"/>) and exactly one
/// thread may call <see cref="Read"/>. Nothing allocates after construction.
/// </summary>
/// <remarks>
/// The write position (head) and read position (tail) are monotonically increasing 64-bit counters on separate cache
/// lines. The producer publishes samples with a release store of the head; the consumer frees space with a release
/// store of the tail. Only <see cref="Volatile"/> reads and writes are used.
/// </remarks>
public sealed class AudioRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private Positions _positions;
    private long _droppedSamples;

    /// <param name="minimumCapacity">Requested capacity in samples; rounded up to the next power of two.</param>
    public AudioRingBuffer(int minimumCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumCapacity, 1 << 30);

        var capacity = (int)BitOperations.RoundUpToPowerOf2((uint)minimumCapacity);
        _buffer = new float[capacity];
        _mask = capacity - 1;
    }

    /// <summary>Capacity in samples (a power of two).</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Samples that can be read right now.</summary>
    public int Available => (int)(Volatile.Read(ref _positions.Head) - Volatile.Read(ref _positions.Tail));

    /// <summary>Samples that can be written right now without dropping.</summary>
    public int FreeSpace => _buffer.Length - Available;

    /// <summary>Samples rejected by <see cref="Write"/> / <see cref="WriteZeros"/> because the ring was full.</summary>
    public long DroppedSamples => Volatile.Read(ref _droppedSamples);

    /// <summary>Samples accepted since construction.</summary>
    public long TotalWritten => Volatile.Read(ref _positions.Head);

    /// <summary>Samples consumed since construction.</summary>
    public long TotalRead => Volatile.Read(ref _positions.Tail);

    /// <summary>Producer only. Writes as many samples as fit; the rest is added to <see cref="DroppedSamples"/>.</summary>
    /// <returns>Number of samples written.</returns>
    public int Write(ReadOnlySpan<float> samples)
    {
        var head = _positions.Head; // only the producer writes the head
        var tail = Volatile.Read(ref _positions.Tail);
        var free = _buffer.Length - (int)(head - tail);
        var count = Math.Min(samples.Length, free);

        if (count > 0)
        {
            var start = (int)head & _mask;
            var first = Math.Min(count, _buffer.Length - start);
            samples[..first].CopyTo(_buffer.AsSpan(start, first));
            if (first < count)
            {
                samples[first..count].CopyTo(_buffer.AsSpan(0, count - first));
            }

            Volatile.Write(ref _positions.Head, head + count);
        }

        if (count < samples.Length)
        {
            Volatile.Write(ref _droppedSamples, _droppedSamples + (samples.Length - count));
        }

        return count;
    }

    /// <summary>Producer only. Writes <paramref name="count"/> zero samples (or as many as fit).</summary>
    /// <returns>Number of samples written.</returns>
    public int WriteZeros(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var head = _positions.Head;
        var tail = Volatile.Read(ref _positions.Tail);
        var free = _buffer.Length - (int)(head - tail);
        var written = Math.Min(count, free);

        if (written > 0)
        {
            var start = (int)head & _mask;
            var first = Math.Min(written, _buffer.Length - start);
            _buffer.AsSpan(start, first).Clear();
            if (first < written)
            {
                _buffer.AsSpan(0, written - first).Clear();
            }

            Volatile.Write(ref _positions.Head, head + written);
        }

        if (written < count)
        {
            Volatile.Write(ref _droppedSamples, _droppedSamples + (count - written));
        }

        return written;
    }

    /// <summary>Consumer only. Reads up to <c>destination.Length</c> samples.</summary>
    /// <returns>Number of samples read.</returns>
    public int Read(Span<float> destination)
    {
        var tail = _positions.Tail; // only the consumer writes the tail
        var head = Volatile.Read(ref _positions.Head);
        var count = Math.Min(destination.Length, (int)(head - tail));

        if (count > 0)
        {
            var start = (int)tail & _mask;
            var first = Math.Min(count, _buffer.Length - start);
            _buffer.AsSpan(start, first).CopyTo(destination);
            if (first < count)
            {
                _buffer.AsSpan(0, count - first).CopyTo(destination[first..count]);
            }

            Volatile.Write(ref _positions.Tail, tail + count);
        }

        return count;
    }

    /// <summary>Head and tail on their own 64-byte cache lines, with a full line of padding around each.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct Positions
    {
        [FieldOffset(64)]
        public long Head;

        [FieldOffset(128)]
        public long Tail;
    }
}
