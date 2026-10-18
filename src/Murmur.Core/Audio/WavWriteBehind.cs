using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Murmur.Core.Audio;

/// <summary>Test seams for <see cref="WavPcm16Writer"/> file writers (production code never sets them).</summary>
internal sealed class WavWriterTuning
{
    /// <summary>Capacity of the write-behind ring in bytes (rounded up to a power of two); <c>null</c> = default.</summary>
    public int? WriteBehindBytes { get; init; }

    /// <summary>Replaces <see cref="RandomAccess.FlushToDisk"/> (e.g. a slow flush that models a stalled disk).</summary>
    public Action<SafeFileHandle>? DurableFlush { get; init; }

    /// <summary>
    /// Runs on the I/O thread before every positional write (data or header), e.g. to make writes wait while a
    /// simulated flush to disk is running (the suspected Windows behaviour of <c>FlushFileBuffers</c>).
    /// </summary>
    public Action<SafeFileHandle>? BeforeWrite { get; init; }
}

/// <summary>
/// Write-behind and durability for one file-backed <see cref="WavPcm16Writer"/>. The producer (the writer thread)
/// converts samples into a lock-free single-producer / single-consumer byte ring and only signals. An I/O thread moves
/// the bytes to the file with positional writes and rewrites the header sizes; a separate durability thread forces the
/// file to the storage device through the <see cref="SafeFileHandle"/> (never through a stream), coalescing requests
/// that arrive while a flush to disk is running into one more flush after it.
/// </summary>
/// <remarks>
/// <para>
/// Why the writes are behind as well (not just the flush to disk): the producer must never touch the file system,
/// because on Windows a seconds-long <c>FlushFileBuffers</c> is suspected to also stall a concurrent <c>WriteFile</c>
/// that extends the same file (CI: max writer cycle 5.72 s against max flush 5.71 s; UNVERIFIED-ON-WINDOWS beyond
/// that observation). Where writes proceed during a flush to disk (Linux, macOS), the I/O thread keeps the OS cache
/// current and a process crash loses almost nothing; where they do not, the stall only grows the ring. If the ring
/// fills up, <see cref="Append"/> waits for space (back-pressure into the capture rings).
/// </para>
/// <para>
/// Ticket protocol: <see cref="RequestDurableFlush"/> returns an increasing ticket. The I/O thread samples the latest
/// requested ticket, then writes every queued byte and the header, and only then publishes the ticket as "written".
/// The durability thread snapshots the published ticket before each flush to disk and completes exactly that ticket
/// afterwards, so a completed ticket always covers every byte appended before it was requested. Nothing on the
/// producer side allocates or takes a lock; the durability thread never holds anything the I/O thread or the producer
/// needs.
/// </para>
/// </remarks>
internal sealed class WavWriteBehind : IDisposable
{
    /// <summary>Default ring: about 65 s of 16 kHz mono PCM16.</summary>
    public const int DefaultCapacityBytes = 1 << 21;

    private const int SpaceWaitMs = 10;

    private readonly SafeFileHandle _handle;
    private readonly long _dataOffset;
    private readonly byte[] _ring;
    private readonly int _mask;
    private readonly Action<SafeFileHandle> _durableFlush;
    private readonly Action<SafeFileHandle>? _beforeWrite;
    private readonly AutoResetEvent _wake = new(false);
    private readonly AutoResetEvent _space = new(false);
    private readonly AutoResetEvent _durableWake = new(false);
    private readonly object _gate = new();
    private readonly byte[] _sizeField = new byte[4];
    private readonly Thread _ioThread;
    private readonly Thread _durableThread;
    private Positions _positions;
    private int _headerRequested;
    private long _durableRequested;
    private long _writtenTicket;
    private long _durableCompleted;
    private long _durableFlushes;
    private long _maxDurableFlushTicks;
    private long _written;
    private long _headerBytes;
    private volatile bool _producerWaiting;
    private volatile bool _stopping;
    private volatile bool _durableStopping;
    private volatile bool _durableExited;
    private volatile Exception? _error;
    private bool _disposed;

    /// <param name="headerDataBytes">Data size the header on disk declares right now.</param>
    public WavWriteBehind(SafeFileHandle handle, long dataOffset, long headerDataBytes, string name, WavWriterTuning? tuning)
    {
        _handle = handle;
        _dataOffset = dataOffset;
        _headerBytes = headerDataBytes;
        var capacity = tuning?.WriteBehindBytes ?? DefaultCapacityBytes;
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 1 << 30);
        _ring = new byte[(int)BitOperations.RoundUpToPowerOf2((uint)capacity)];
        _mask = _ring.Length - 1;
        _durableFlush = tuning?.DurableFlush ?? RandomAccess.FlushToDisk;
        _beforeWrite = tuning?.BeforeWrite;
        _ioThread = new Thread(RunIo)
        {
            IsBackground = true,
            Name = name,
        };
        _durableThread = new Thread(RunDurable)
        {
            IsBackground = true,
            Name = name + " (durable)",
        };
        _durableThread.Start();
        _ioThread.Start();
    }

    /// <summary>Ring capacity in bytes (a power of two).</summary>
    public int CapacityBytes => _ring.Length;

    /// <summary>Bytes appended but not yet handed to the OS.</summary>
    public int PendingBytes => (int)(Volatile.Read(ref _positions.Head) - Volatile.Read(ref _positions.Tail));

    /// <summary>Flushes to disk completed so far (coalesced requests count once).</summary>
    public long DurableFlushes => Volatile.Read(ref _durableFlushes);

    /// <summary>Longest flush to disk so far.</summary>
    public TimeSpan MaxDurableFlush => Stopwatch.GetElapsedTime(0, Volatile.Read(ref _maxDurableFlushTicks));

    /// <summary>Producer only. Converts to PCM16 into the ring (waits for space if it is full) and wakes the I/O thread.</summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        ThrowIfFailed();
        while (!samples.IsEmpty)
        {
            var head = _positions.Head; // only the producer writes the head
            var free = _ring.Length - (int)(head - Volatile.Read(ref _positions.Tail));
            var count = Math.Min(samples.Length, free / 2);
            if (count == 0)
            {
                WaitForSpace();
                continue;
            }

            // The head is always even and the capacity is a power of two, so a sample never straddles the wrap.
            var start = (int)head & _mask;
            var first = Math.Min(count, (_ring.Length - start) / 2);
            WavPcm16Writer.Convert(samples[..first], _ring.AsSpan(start, first * 2));
            if (first < count)
            {
                WavPcm16Writer.Convert(samples[first..count], _ring.AsSpan(0, (count - first) * 2));
            }

            Volatile.Write(ref _positions.Head, head + (count * 2L));
            samples = samples[count..];
        }

        _wake.Set();
    }

    /// <summary>Asks the I/O thread to rewrite the header sizes after writing everything queued so far. Never blocks.</summary>
    public void RequestHeader()
    {
        ThrowIfFailed();
        Volatile.Write(ref _headerRequested, 1);
        _wake.Set();
    }

    /// <summary>
    /// Asks for everything queued so far plus the header to be written and then flushed to disk. Never blocks; any
    /// thread.
    /// </summary>
    /// <returns>Ticket for <see cref="WaitForDurableFlush"/>.</returns>
    public long RequestDurableFlush()
    {
        ThrowIfFailed();
        var ticket = Interlocked.Increment(ref _durableRequested);
        _wake.Set();
        return ticket;
    }

    /// <summary>Blocks until the flush to disk for <paramref name="ticket"/> (or a later one) has completed. Any thread but the producer's hot path.</summary>
    public void WaitForDurableFlush(long ticket)
    {
        lock (_gate)
        {
            while (Volatile.Read(ref _durableCompleted) < ticket && !_durableExited && _error is null)
            {
                Monitor.Wait(_gate);
            }
        }

        ThrowIfFailed();
        if (Volatile.Read(ref _durableCompleted) < ticket)
        {
            throw new ObjectDisposedException(nameof(WavPcm16Writer));
        }
    }

    /// <summary>
    /// Writes everything still queued and the header, runs a final flush to disk, then stops both background threads.
    /// </summary>
    /// <exception cref="IOException">A background thread failed at some point; the file may be incomplete.</exception>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        _wake.Set();
        _ioThread.Join(); // its final pass has written and published everything
        _durableStopping = true;
        _durableWake.Set();
        _durableThread.Join();
        _wake.Dispose();
        _space.Dispose();
        _durableWake.Dispose();
        ThrowIfFailed();
    }

    private void ThrowIfFailed()
    {
        if (_error is { } error)
        {
            throw new IOException("Writing the WAV file in the background failed: " + error.Message, error);
        }
    }

    private void Fail(Exception ex)
    {
        lock (_gate)
        {
            _error ??= ex;
            Monitor.PulseAll(_gate);
        }
    }

    private void WaitForSpace()
    {
        _producerWaiting = true;
        _wake.Set();
        _space.WaitOne(SpaceWaitMs); // bounded, so a missed wake-up only costs a few milliseconds
        _producerWaiting = false;
        ThrowIfFailed();
    }

    private void RunIo()
    {
        try
        {
            while (true)
            {
                _wake.WaitOne();
                var stopping = _stopping; // read before pumping: the final pass then sees every appended byte
                Pump(stopping);
                if (stopping)
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            _space.Set();
        }
    }

    private void Pump(bool final)
    {
        // Sample the requests before writing, so whatever was appended before a request is written before serving it.
        var durableTarget = Volatile.Read(ref _durableRequested);
        var durable = durableTarget > _writtenTicket; // only this thread writes _writtenTicket
        var header = Interlocked.Exchange(ref _headerRequested, 0) != 0 || durable || final;

        WriteQueued();
        if (header)
        {
            WriteHeaderSizes();
        }

        if (durable)
        {
            // Publish only now: the durability thread must never flush for a ticket whose bytes are not written yet.
            Volatile.Write(ref _writtenTicket, durableTarget);
            _durableWake.Set();
        }
    }

    private void WriteQueued()
    {
        var tail = _positions.Tail; // only this thread writes the tail
        var head = Volatile.Read(ref _positions.Head);
        while (tail < head)
        {
            var start = (int)tail & _mask;
            var count = (int)Math.Min(head - tail, _ring.Length - start);
            _beforeWrite?.Invoke(_handle);
            RandomAccess.Write(_handle, new ReadOnlySpan<byte>(_ring, start, count), _dataOffset + _written);
            _written += count;
            tail += count;
            Volatile.Write(ref _positions.Tail, tail);
            if (_producerWaiting)
            {
                _space.Set();
            }
        }
    }

    private void WriteHeaderSizes()
    {
        if (_headerBytes == _written)
        {
            return;
        }

        _beforeWrite?.Invoke(_handle);
        var data = (uint)Math.Min(_written, uint.MaxValue - 36);
        BinaryPrimitives.WriteUInt32LittleEndian(_sizeField, data + 36);
        RandomAccess.Write(_handle, _sizeField, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(_sizeField, data);
        RandomAccess.Write(_handle, _sizeField, 40);
        _headerBytes = _written;
    }

    private void RunDurable()
    {
        try
        {
            while (true)
            {
                _durableWake.WaitOne();

                // Coalescing: tickets published while a flush was running are served by one more flush right after it.
                while (true)
                {
                    var stopping = _durableStopping; // set only after the I/O thread's final pass, so read it first
                    var target = Volatile.Read(ref _writtenTicket);
                    if (stopping && _error is not null)
                    {
                        return;
                    }

                    if (!stopping && target <= _durableCompleted)
                    {
                        break;
                    }

                    FlushToDisk(target);
                    if (stopping)
                    {
                        return; // the final flush to disk is done
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            lock (_gate)
            {
                _durableExited = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void FlushToDisk(long target)
    {
        var start = Stopwatch.GetTimestamp();
        _durableFlush(_handle);
        var ticks = Stopwatch.GetTimestamp() - start;
        Volatile.Write(ref _maxDurableFlushTicks, Math.Max(_maxDurableFlushTicks, ticks));
        Volatile.Write(ref _durableFlushes, _durableFlushes + 1);
        lock (_gate)
        {
            Volatile.Write(ref _durableCompleted, Math.Max(_durableCompleted, target)); // only this thread writes it
            Monitor.PulseAll(_gate);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct Positions
    {
        [FieldOffset(64)]
        public long Head;

        [FieldOffset(128)]
        public long Tail;
    }
}
