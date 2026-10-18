using Murmur.Core.Audio;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Audio;

public sealed class AudioRingBufferTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 4)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 2048)]
    public void Capacity_IsRoundedUpToAPowerOfTwo(int requested, int expected)
    {
        Assert.Equal(expected, new AudioRingBuffer(requested).Capacity);
    }

    [Fact]
    public void WriteThenRead_ReturnsTheSameSamples()
    {
        var ring = new AudioRingBuffer(16);
        float[] data = [1, 2, 3, 4, 5];

        Assert.Equal(5, ring.Write(data));
        Assert.Equal(5, ring.Available);
        Assert.Equal(11, ring.FreeSpace);

        var read = new float[8];
        Assert.Equal(5, ring.Read(read));
        Assert.Equal(data, read[..5]);
        Assert.Equal(0, ring.Available);
        Assert.Equal(0, ring.Read(read));
    }

    [Fact]
    public void Wraparound_KeepsOrder()
    {
        var ring = new AudioRingBuffer(8);
        var buffer = new float[8];
        float next = 0;
        float expected = 0;

        // Odd chunk sizes force writes and reads to straddle the end of the array many times.
        for (var round = 0; round < 100; round++)
        {
            var chunk = new float[(round % 5) + 1];
            for (var i = 0; i < chunk.Length; i++)
            {
                chunk[i] = next++;
            }

            Assert.Equal(chunk.Length, ring.Write(chunk));
            var n = ring.Read(buffer);
            for (var i = 0; i < n; i++)
            {
                Assert.Equal(expected++, buffer[i]);
            }
        }

        Assert.Equal(next, expected);
        Assert.Equal(0, ring.DroppedSamples);
    }

    [Fact]
    public void Overflow_WritesWhatFitsAndCountsTheRest()
    {
        var ring = new AudioRingBuffer(8);

        Assert.Equal(6, ring.Write(new float[6]));
        Assert.Equal(2, ring.Write([1, 2, 3, 4, 5]));
        Assert.Equal(3, ring.DroppedSamples);
        Assert.Equal(0, ring.Write([9]));
        Assert.Equal(4, ring.DroppedSamples);
        Assert.Equal(0, ring.WriteZeros(10));
        Assert.Equal(14, ring.DroppedSamples);

        var read = new float[8];
        Assert.Equal(8, ring.Read(read));
        Assert.Equal([0, 0, 0, 0, 0, 0, 1, 2], read);
        Assert.Equal(8, ring.TotalWritten);
        Assert.Equal(8, ring.TotalRead);
    }

    [Fact]
    public void WriteZeros_WritesSilenceAcrossTheWrap()
    {
        var ring = new AudioRingBuffer(8);
        ring.Write([1, 1, 1, 1, 1, 1]);
        ring.Read(new float[6]);

        Assert.Equal(5, ring.WriteZeros(5));
        var read = new float[5];
        Array.Fill(read, 7f);
        Assert.Equal(5, ring.Read(read));
        Assert.All(read, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void WriteAndRead_DoNotAllocate()
    {
        var ring = new AudioRingBuffer(4096);
        var chunk = new float[960];
        var read = new float[960];
        ring.Write(chunk);
        ring.Read(read);

        var allocated = Allocations.SteadyState(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                ring.Write(chunk);
                ring.WriteZeros(10);
                ring.Read(read);
                ring.Read(read.AsSpan(0, 10));
            }
        });
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task TwoThreadStress_DeliversEverySampleInOrder()
    {
        const long total = 100_000_000;
        const int modulus = 1 << 24; // floats represent every integer below 2^24 exactly
        var ring = new AudioRingBuffer(1 << 14);
        var failure = -1L;

        var producer = new Thread(() =>
        {
            var chunk = new float[1021];
            long sent = 0;
            var size = 1;
            while (sent < total)
            {
                size = (size * 7 % chunk.Length) + 1;
                var count = (int)Math.Min(size, total - sent);
                for (var i = 0; i < count; i++)
                {
                    chunk[i] = (sent + i) % modulus;
                }

                var offset = 0;
                while (offset < count)
                {
                    // Write only what fits so the stress test exercises full-ring back-off instead of drops.
                    var fit = Math.Min(count - offset, ring.FreeSpace);
                    if (fit == 0)
                    {
                        Thread.SpinWait(20);
                        continue;
                    }

                    offset += ring.Write(chunk.AsSpan(offset, fit));
                }

                sent += count;
            }
        });

        var consumer = new Thread(() =>
        {
            var buffer = new float[777];
            long received = 0;
            while (received < total)
            {
                var n = ring.Read(buffer);
                if (n == 0)
                {
                    Thread.SpinWait(20);
                    continue;
                }

                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] != (received + i) % modulus)
                    {
                        Interlocked.CompareExchange(ref failure, received + i, -1);
                        return;
                    }
                }

                received += n;
            }
        });

        producer.Start();
        consumer.Start();
        await Task.Run(() =>
        {
            consumer.Join();
            if (Interlocked.Read(ref failure) >= 0)
            {
                // The producer may be stuck on a full ring once the consumer bailed out.
                return;
            }

            producer.Join();
        }, TestContext.Current.CancellationToken);

        Assert.Equal(-1, Interlocked.Read(ref failure));
        Assert.Equal(total, ring.TotalWritten);
        Assert.Equal(total, ring.TotalRead);
        Assert.Equal(0, ring.DroppedSamples);
    }
}
