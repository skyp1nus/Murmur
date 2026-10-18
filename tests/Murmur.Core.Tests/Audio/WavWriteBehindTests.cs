using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Murmur.Core.Audio;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Audio;

/// <summary>
/// The appending thread of a file-backed <see cref="WavPcm16Writer"/> must never wait for the disk: a flush to disk that
/// takes seconds (as observed on Windows CI) is simulated with an injected slow durable flush.
/// </summary>
public sealed class WavWriteBehindTests : IDisposable
{
    private static readonly TimeSpan SlowFlush = TimeSpan.FromSeconds(3);
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void SlowDurableFlush_DoesNotBlockAppendOrFlush_AndRequestsCoalesce()
    {
        var path = _dir.File("slow.wav");
        var flushes = 0;
        var slow = true;
        var tuning = new WavWriterTuning
        {
            DurableFlush = handle =>
            {
                Interlocked.Increment(ref flushes);
                if (Volatile.Read(ref slow))
                {
                    Thread.Sleep(SlowFlush);
                }

                RandomAccess.FlushToDisk(handle);
            },
        };

        var writer = new WavPcm16Writer(path, 16_000, 1, tuning);
        var chunk = Ramp(320); // 20 ms, the recorder's writer period
        writer.Append(Ramp(16_000));
        writer.Flush(); // starts a 3 s flush to disk
        WaitUntil(() => Volatile.Read(ref flushes) == 1);

        // One second of 20 ms writer cycles while the disk is "stalled".
        var worst = TimeSpan.Zero;
        var cycles = 0;
        var during = Stopwatch.StartNew();
        while (during.Elapsed < TimeSpan.FromSeconds(1))
        {
            var start = Stopwatch.GetTimestamp();
            writer.Append(chunk);
            writer.UpdateHeader();
            writer.Flush();
            var took = Stopwatch.GetElapsedTime(start);
            worst = took > worst ? took : worst;
            cycles++;
            Thread.Sleep(20);
        }

        Assert.True(worst < TimeSpan.FromMilliseconds(50), $"slowest Append+UpdateHeader+Flush took {worst.TotalMilliseconds:F1} ms");
        var expectedSamples = 16_000 + (cycles * chunk.Length);
        Assert.Equal(expectedSamples, writer.SamplesWritten);

        // Process-crash safety does not wait for the stalled flush to disk: the samples and the header reach the OS
        // cache while it is still running.
        WaitUntil(() => writer.WriteBehind!.PendingBytes == 0);
        WaitForDeclaredBytes(path, expectedSamples * 2L);
        Assert.Equal(1, Volatile.Read(ref flushes)); // the first flush is still running; every request since is coalesced

        // A blocking flush waits for the running flush and one coalesced flush that covers everything appended.
        var wait = Stopwatch.StartNew();
        writer.FlushToDisk();
        Assert.True(wait.Elapsed >= SlowFlush - TimeSpan.FromSeconds(2), $"FlushToDisk returned after {wait.Elapsed.TotalSeconds:F2} s");
        Assert.Equal(2, Volatile.Read(ref flushes));
        Assert.Equal(0, writer.WriteBehind!.PendingBytes);
        Assert.True(writer.WriteBehind.MaxDurableFlush >= SlowFlush);

        using (var reader = WavReader.Open(path))
        {
            Assert.Equal(expectedSamples * 2L, reader.Layout.DeclaredDataBytes);
            Assert.Equal(expectedSamples, reader.FrameCount);
        }

        Volatile.Write(ref slow, false);
        writer.Dispose();
        Assert.Equal(3, Volatile.Read(ref flushes)); // final flush on dispose

        var (_, samples) = WavReader.ReadAll(path);
        Assert.Equal(expectedSamples, samples.Length);
        Assert.Equal(chunk[^1], samples[^1], 1e-4f);
    }

    /// <summary>
    /// Models the suspected Windows behaviour (UNVERIFIED-ON-WINDOWS): writes to the file wait while a flush to disk is
    /// running. The appending thread still never waits; the ring holds the samples until the flush is over.
    /// </summary>
    [Fact]
    public void WritesStalledByASlowDurableFlush_OnlyGrowTheRing()
    {
        var path = _dir.File("stalled-writes.wav");
        using var writesAllowed = new ManualResetEventSlim(true);
        using var release = new ManualResetEventSlim(false);
        var flushes = 0;
        var tuning = new WavWriterTuning
        {
            BeforeWrite = _ => writesAllowed.Wait(),
            DurableFlush = handle =>
            {
                writesAllowed.Reset();
                Interlocked.Increment(ref flushes);
                release.Wait(TimeSpan.FromSeconds(30));
                RandomAccess.FlushToDisk(handle);
                writesAllowed.Set();
            },
        };

        var writer = new WavPcm16Writer(path, 16_000, 1, tuning);
        try
        {
            writer.Append(Ramp(1_600));
            writer.Flush();
            WaitUntil(() => Volatile.Read(ref flushes) == 1);

            var chunk = Ramp(320);
            var worst = TimeSpan.Zero;
            for (var i = 0; i < 25; i++)
            {
                var start = Stopwatch.GetTimestamp();
                writer.Append(chunk);
                writer.Flush();
                var took = Stopwatch.GetElapsedTime(start);
                worst = took > worst ? took : worst;
                Thread.Sleep(20);
            }

            Assert.True(worst < TimeSpan.FromMilliseconds(50), $"slowest Append+Flush took {worst.TotalMilliseconds:F1} ms");
            Assert.True(writer.WriteBehind!.PendingBytes >= 24 * chunk.Length * 2, $"only {writer.WriteBehind.PendingBytes} bytes held back");
        }
        finally
        {
            release.Set();
        }

        writer.FlushToDisk();
        Assert.Equal(0, writer.WriteBehind!.PendingBytes);
        writer.Dispose();

        var (_, samples) = WavReader.ReadAll(path);
        Assert.Equal(1_600 + (25 * 320), samples.Length);
    }

    /// <summary>Whenever the durability thread flushes, the file already holds every byte requested before, header included.</summary>
    [Fact]
    public void DurableFlush_RunsOnlyAfterTheRequestedBytesAndHeaderAreWritten()
    {
        var observed = new List<(long Length, uint Declared)>();
        var tuning = new WavWriterTuning
        {
            DurableFlush = handle =>
            {
                Span<byte> field = stackalloc byte[4];
                RandomAccess.Read(handle, field, 40);
                lock (observed)
                {
                    observed.Add((RandomAccess.GetLength(handle), BinaryPrimitives.ReadUInt32LittleEndian(field)));
                }

                Thread.Sleep(5);
            },
        };

        using var writer = new WavPcm16Writer(_dir.File("ticket.wav"), 16_000, 1, tuning);
        for (var i = 1; i <= 50; i++)
        {
            writer.Append(Ramp(160));
            if (i % 10 == 0)
            {
                writer.FlushToDisk();
                (long Length, uint Declared) last;
                lock (observed)
                {
                    last = observed[^1];
                }

                var bytes = i * 160 * 2;
                Assert.True(last.Length >= WavPcm16Writer.HeaderSize + bytes, $"flush ran at length {last.Length}, expected {WavPcm16Writer.HeaderSize + bytes}");
                Assert.True(last.Declared >= bytes, $"flush ran with header {last.Declared}, expected {bytes}");
            }
            else
            {
                writer.Flush();
            }
        }
    }

    [Fact]
    public void SlowDurableFlush_AppendStillDoesNotAllocate()
    {
        var slow = new ManualResetEventSlim(false);
        var tuning = new WavWriterTuning
        {
            DurableFlush = handle =>
            {
                slow.Wait(TimeSpan.FromSeconds(30));
                RandomAccess.FlushToDisk(handle);
            },
        };

        using var writer = new WavPcm16Writer(_dir.File("alloc-slow.wav"), 16_000, 1, tuning);
        var chunk = new float[320];
        try
        {
            writer.Append(chunk);
            writer.Flush();

            var allocated = Allocations.SteadyState(() =>
            {
                for (var i = 0; i < 200; i++)
                {
                    writer.Append(chunk);
                    writer.UpdateHeader();
                    writer.Flush();
                }
            });
            Assert.Equal(0, allocated);
        }
        finally
        {
            slow.Set();
        }
    }

    [Fact]
    public void TinyRing_WrapsAndAppliesBackPressure_WithoutLosingSamples()
    {
        var path = _dir.File("tiny.wav");
        var input = Ramp(100_003);
        using (var writer = new WavPcm16Writer(path, 16_000, 1, new WavWriterTuning { WriteBehindBytes = 64 }))
        {
            Assert.Equal(64, writer.WriteBehind!.CapacityBytes);
            var span = input.AsSpan();
            var step = 1;
            while (!span.IsEmpty)
            {
                var take = Math.Min(span.Length, step);
                writer.Append(span[..take]);
                span = span[take..];
                step = (step * 7 % 97) + 1;
            }
        }

        var (_, samples) = WavReader.ReadAll(path);
        Assert.Equal(input.Length, samples.Length);
        for (var i = 0; i < input.Length; i++)
        {
            Assert.Equal(input[i], samples[i], 1e-4f);
        }
    }

    [Fact]
    public void BackgroundFailure_SurfacesOnTheNextCalls()
    {
        var tuning = new WavWriterTuning { DurableFlush = static _ => throw new IOException("disk gone") };
        var writer = new WavPcm16Writer(_dir.File("fail.wav"), 16_000, 1, tuning);
        writer.Append(new float[160]);

        var flush = Assert.Throws<IOException>(writer.FlushToDisk);
        Assert.Contains("disk gone", flush.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(flush.InnerException);
        Assert.Throws<IOException>(() => writer.Append(new float[160]));
        Assert.Throws<IOException>(writer.Flush);
        Assert.Throws<IOException>(writer.Dispose);
        Assert.Throws<ObjectDisposedException>(() => writer.Append(new float[1]));
    }

    [Fact]
    public void FlushToDisk_FromAnotherThread_WhileAppending_KeepsEverySample()
    {
        var path = _dir.File("mt.wav");
        var durable = 0;
        var tuning = new WavWriterTuning
        {
            DurableFlush = handle =>
            {
                Interlocked.Increment(ref durable);
                Thread.Sleep(30);
                RandomAccess.FlushToDisk(handle);
            },
        };

        var input = Ramp(160_000);
        using (var writer = new WavPcm16Writer(path, 16_000, 1, tuning))
        {
            using var stop = new ManualResetEventSlim(false);
            var flusher = new Thread(() =>
            {
                while (!stop.IsSet)
                {
                    writer.FlushToDisk();
                }
            });
            flusher.Start();
            for (var i = 0; i < input.Length; i += 1_600)
            {
                writer.Append(input.AsSpan(i, 1_600));
                writer.UpdateHeader();
                Thread.Sleep(1);
            }

            stop.Set();
            flusher.Join();
        }

        Assert.True(Volatile.Read(ref durable) >= 2);
        var (_, samples) = WavReader.ReadAll(path);
        Assert.Equal(input.Length, samples.Length);
        for (var i = 0; i < input.Length; i += 101)
        {
            Assert.Equal(input[i], samples[i], 1e-4f);
        }
    }

    [Fact]
    public void Tuning_FlowsToWritersCreatedInTheSameAsyncFlow()
    {
        var calls = 0;
        var tuning = new WavWriterTuning
        {
            WriteBehindBytes = 1 << 12,
            DurableFlush = handle =>
            {
                Interlocked.Increment(ref calls);
                RandomAccess.FlushToDisk(handle);
            },
        };

        WavPcm16Writer.Tuning.Value = tuning;
        try
        {
            using var writer = new WavPcm16Writer(_dir.File("tuned.wav"));
            Assert.Equal(1 << 12, writer.WriteBehind!.CapacityBytes);
            writer.FlushToDisk();
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            WavPcm16Writer.Tuning.Value = null;
        }

        using var plain = new WavPcm16Writer(_dir.File("plain.wav"));
        Assert.Equal(WavWriteBehind.DefaultCapacityBytes, plain.WriteBehind!.CapacityBytes);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndRunsExactlyOneFinalDurableFlush()
    {
        var flushes = 0;
        var tuning = new WavWriterTuning { DurableFlush = h => { Interlocked.Increment(ref flushes); RandomAccess.FlushToDisk(h); } };
        var path = _dir.File("dispose.wav");
        var writer = new WavPcm16Writer(path, 16_000, 1, tuning);
        writer.Append(Ramp(800));
        writer.Dispose();
        writer.Dispose();
        Assert.Equal(1, Volatile.Read(ref flushes));
        var (_, samples) = WavReader.ReadAll(path);
        Assert.Equal(800, samples.Length);
    }

    [Fact]
    public void Dispose_WhileDurableFlushIsStalled_WaitsForItAndKeepsEverySample()
    {
        var path = _dir.File("dispose-stalled.wav");
        var started = new ManualResetEventSlim(false);
        var tuning = new WavWriterTuning
        {
            DurableFlush = h =>
            {
                started.Set();
                Thread.Sleep(300);
                RandomAccess.FlushToDisk(h);
            },
        };
        var writer = new WavPcm16Writer(path, 16_000, 1, tuning);
        writer.Append(Ramp(1_600));
        writer.Flush();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        writer.Append(Ramp(1_600));
        writer.Dispose();
        var (_, samples) = WavReader.ReadAll(path);
        Assert.Equal(3_200, samples.Length);
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "condition not reached");
            Thread.Sleep(1);
        }
    }

    /// <summary>Polls the header of a file that the background I/O thread is updating.</summary>
    private static void WaitForDeclaredBytes(string path, long expected)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            using (var reader = WavReader.Open(path))
            {
                if (reader.Layout.DeclaredDataBytes == expected)
                {
                    return;
                }
            }

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the header never declared {expected} bytes");
            Thread.Sleep(1);
        }
    }

    private static float[] Ramp(int count)
    {
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = ((i % 1000) / 1000f) - 0.5f;
        }

        return samples;
    }
}
