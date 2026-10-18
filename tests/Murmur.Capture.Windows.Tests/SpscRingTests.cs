using Murmur.Capture.Windows.Screen;

namespace Murmur.Capture.Windows.Tests;

public sealed class SpscRingTests
{
    [Fact]
    public void Enqueue_FailsWhenFull_AndDequeueIsFifo()
    {
        var ring = new SpscRing<long>(3);
        Assert.Equal(3, ring.Capacity);
        Assert.True(ring.TryEnqueue(1));
        Assert.True(ring.TryEnqueue(2));
        Assert.True(ring.TryEnqueue(3));
        Assert.False(ring.TryEnqueue(4)); // capacity 3 even though the backing array is 4
        Assert.Equal(3, ring.Count);

        Assert.True(ring.TryDequeue(out var a));
        Assert.True(ring.TryEnqueue(5));
        Assert.True(ring.TryDequeue(out var b));
        Assert.True(ring.TryDequeue(out var c));
        Assert.True(ring.TryDequeue(out var d));
        Assert.False(ring.TryDequeue(out var none));

        Assert.Equal([1L, 2L, 3L, 5L], [a, b, c, d]);
        Assert.Equal(0L, none);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Wraps_ManyTimes()
    {
        var ring = new SpscRing<int>(4);
        var next = 0;
        for (var i = 0; i < 10_000; i++)
        {
            Assert.True(ring.TryEnqueue(i));
            if (i % 3 == 2)
            {
                while (ring.TryDequeue(out var value))
                {
                    Assert.Equal(next++, value);
                }
            }
        }
    }

    [Fact]
    public void ConcurrentProducerAndConsumer_LoseNothingAndKeepOrder()
    {
        const int Count = 200_000;
        var ring = new SpscRing<long>(8);
        var dropped = 0L;
        var received = new List<long>(Count);
        var done = false;

        var consumer = new Thread(() =>
        {
            while (!Volatile.Read(ref done) || ring.Count > 0)
            {
                while (ring.TryDequeue(out var value))
                {
                    received.Add(value);
                }
            }
        });
        consumer.Start();

        for (long i = 0; i < Count; i++)
        {
            if (!ring.TryEnqueue(i))
            {
                dropped++;
            }
        }

        Volatile.Write(ref done, true);
        consumer.Join();

        Assert.Equal(Count, received.Count + dropped);
        for (var i = 1; i < received.Count; i++)
        {
            Assert.True(received[i] > received[i - 1]);
        }
    }

    [Fact]
    public void RejectsZeroCapacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpscRing<int>(0));
}
