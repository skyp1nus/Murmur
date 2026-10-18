namespace Murmur.Capture.Windows.Screen;

/// <summary>
/// Bounded lock-free single-producer / single-consumer queue of unmanaged items. <see cref="TryEnqueue"/> never
/// blocks: it fails when the ring is full so the producer (the FrameArrived handler) can drop and count the frame.
/// One producer and one consumer at a time; the producer may change threads if calls are serialized with a full
/// fence (the FrameArrived re-entrancy guard is an <see cref="Interlocked"/> exchange). Allocation-free after
/// construction.
/// </summary>
internal sealed class SpscRing<T>
    where T : unmanaged
{
    private readonly T[] _items;
    private readonly int _mask;
    private long _head; // next slot to read (consumer-owned)
    private long _tail; // next slot to write (producer-owned)

    public SpscRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)capacity);
        _items = new T[size];
        _mask = size - 1;
        Capacity = capacity;
    }

    /// <summary>The configured capacity (the backing array may be larger, rounded up to a power of two).</summary>
    public int Capacity { get; }

    public int Count => (int)(Volatile.Read(ref _tail) - Volatile.Read(ref _head));

    public bool TryEnqueue(T item)
    {
        var tail = _tail;
        if (tail - Volatile.Read(ref _head) >= Capacity)
        {
            return false;
        }

        _items[tail & _mask] = item;
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }

    public bool TryDequeue(out T item)
    {
        var head = _head;
        if (head >= Volatile.Read(ref _tail))
        {
            item = default;
            return false;
        }

        item = _items[head & _mask];
        Volatile.Write(ref _head, head + 1);
        return true;
    }
}
