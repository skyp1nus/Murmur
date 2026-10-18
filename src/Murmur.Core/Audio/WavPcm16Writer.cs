using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Murmur.Core.Audio;

/// <summary>
/// Crash-safe 16-bit PCM WAV writer (canonical 44-byte header, 16 kHz mono by default).
/// </summary>
/// <remarks>
/// <para>
/// <b>The appending thread never waits for the disk.</b> A file-backed writer converts every <see cref="Append"/>
/// into a preallocated lock-free ring (about 65 s of 16 kHz mono) and only signals. A background I/O thread hands the
/// bytes to the OS with positional writes right away and rewrites the RIFF/data sizes when asked
/// (<see cref="UpdateHeader"/>, <see cref="Flush"/>); a separate background durability thread forces the file to the
/// storage device through the file handle when asked (<see cref="Flush"/>, <see cref="FlushToDisk"/>). Requests that
/// arrive while a flush to disk is running are coalesced into one more flush after it. A seconds-long
/// <c>FlushFileBuffers</c> / <c>fsync</c> therefore delays neither the appending thread nor the writes to the OS cache;
/// if the OS also stalls the writes during it (suspected on Windows, UNVERIFIED-ON-WINDOWS), the stall only grows the
/// ring, and only when the ring is full does <see cref="Append"/> wait for space. Nothing on the appending thread
/// allocates or takes a lock after construction.
/// </para>
/// <para>
/// <b>Durability model</b> (file-backed writer whose owner calls <see cref="Flush"/>, or <see cref="UpdateHeader"/> +
/// <see cref="FlushToDisk"/>, every flush interval <c>F</c>):
/// a killed <i>process</i> loses at most about one flush interval: the samples are in the OS cache within
/// milliseconds of <see cref="Append"/> (independently of any running flush to disk, unless the OS itself stalls
/// writes), the header lags by at most <c>F</c>, and <see cref="WavRepair.TryRepair"/> recomputes the sizes from the
/// file length; on top of that only what the owner had not appended yet is lost.
/// An <i>OS crash or power loss</i> loses at most <c>F</c> + the latency of the flush to disk (a coalesced request
/// may also wait for the flush that was already running).
/// </para>
/// <para>
/// A writer over a caller-provided <see cref="Stream"/> is synchronous (no I/O thread): <see cref="Append"/> writes to
/// the stream, <see cref="Flush"/> does not force anything to disk and <see cref="FlushToDisk"/> runs on the caller's
/// thread.
/// </para>
/// <para>
/// Threading: one thread appends, updates the header and calls <see cref="Flush"/>; <see cref="FlushToDisk"/> of a
/// file-backed writer may be called from any other thread at the same time; <see cref="Dispose"/> must not race with
/// anything.
/// </para>
/// </remarks>
public sealed class WavPcm16Writer : IDisposable
{
    /// <summary>Size of the canonical PCM header written by this class.</summary>
    public const int HeaderSize = 44;

    private const int ConvertChunkSamples = 4096;

    private readonly SafeFileHandle? _handle;
    private readonly WavWriteBehind? _behind;
    private readonly Stream? _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _bytes = new byte[ConvertChunkSamples * 2];
    private readonly byte[] _sizeField = new byte[4];
    private long _dataBytes;
    private long _headerDataBytes = -1;
    private volatile bool _disposed;

    /// <summary>
    /// Creates (or truncates) <paramref name="path"/>, writes the header with zero sizes, flushes it to disk and starts
    /// the background I/O and durability threads.
    /// </summary>
    public WavPcm16Writer(string path, int sampleRate = 16_000, int channels = 1)
        : this(path, sampleRate, channels, Tuning.Value)
    {
    }

    internal WavPcm16Writer(string path, int sampleRate, int channels, WavWriterTuning? tuning)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Validate(sampleRate, channels);
        SampleRate = sampleRate;
        Channels = channels;
        Path = path;
        _handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            WriteInitialHeader();
            _behind = new WavWriteBehind(_handle, HeaderSize, headerDataBytes: 0, "Murmur WAV I/O", tuning);
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    /// <summary>Writes to a seekable <paramref name="stream"/>, header at position 0.</summary>
    public WavPcm16Writer(Stream stream, int sampleRate = 16_000, int channels = 1, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Validate(sampleRate, channels);
        if (!stream.CanSeek || !stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable and seekable.", nameof(stream));
        }

        SampleRate = sampleRate;
        Channels = channels;
        _stream = stream;
        _leaveOpen = leaveOpen;
        WriteInitialHeader();
    }

    /// <summary>File path, or <c>null</c> when writing to a caller-provided stream.</summary>
    public string? Path { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Samples (not frames) appended so far.</summary>
    public long SamplesWritten => _dataBytes / 2;

    /// <summary>Audio appended so far.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)(_dataBytes / 2 / Channels) / SampleRate);

    /// <summary>
    /// Test seam: tuning picked up by the file-backed writers created in the current async flow (so it also reaches
    /// writers created indirectly, e.g. by the recorder). Never set in production.
    /// </summary>
    internal static AsyncLocal<WavWriterTuning?> Tuning { get; } = new();

    /// <summary>Background write-behind of a file-backed writer (diagnostics and tests); <c>null</c> for a stream.</summary>
    internal WavWriteBehind? WriteBehind => _behind;

    /// <summary>
    /// Converts float samples in [-1, 1] (clipped; NaN = 0) to PCM16 and appends them. A file-backed writer never waits
    /// for the disk here unless its write-behind ring is full.
    /// </summary>
    /// <exception cref="IOException">The background I/O thread of a file-backed writer failed.</exception>
    public void Append(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_behind is not null)
        {
            _behind.Append(samples);
            _dataBytes += samples.Length * 2L;
            return;
        }

        while (!samples.IsEmpty)
        {
            var count = Math.Min(samples.Length, ConvertChunkSamples);
            var bytes = _bytes.AsSpan(0, count * 2);
            Convert(samples[..count], bytes);
            WriteAt(HeaderSize + _dataBytes, bytes);
            _dataBytes += bytes.Length;
            samples = samples[count..];
        }
    }

    /// <summary>
    /// Rewrites the RIFF and data sizes to match what was appended (cheap; no flush to disk). A file-backed writer only
    /// queues the rewrite (never blocks): its I/O thread does it right after writing the samples queued before it.
    /// </summary>
    public void UpdateHeader()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_headerDataBytes == _dataBytes)
        {
            return;
        }

        if (_behind is not null)
        {
            _behind.RequestHeader();
            _headerDataBytes = _dataBytes;
            return;
        }

        var data = (uint)Math.Min(_dataBytes, uint.MaxValue - 36);
        BinaryPrimitives.WriteUInt32LittleEndian(_sizeField, data + 36);
        WriteAt(4, _sizeField);
        BinaryPrimitives.WriteUInt32LittleEndian(_sizeField, data);
        WriteAt(40, _sizeField);
        _headerDataBytes = _dataBytes;
    }

    /// <summary>
    /// Forces everything appended so far (for a file-backed writer also the header sizes) to the storage device and
    /// waits for it. May block for a long time on a busy disk: never call it from a real-time thread (use
    /// <see cref="Flush"/>, which requests the same without waiting).
    /// </summary>
    public void FlushToDisk()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_behind is not null)
        {
            _behind.WaitForDurableFlush(_behind.RequestDurableFlush());
        }
        else if (_stream is FileStream file)
        {
            file.Flush(flushToDisk: true);
        }
        else
        {
            _stream!.Flush();
        }
    }

    /// <summary>
    /// Header sizes + flush to disk, without waiting. A file-backed writer queues both: its I/O thread writes the queued
    /// samples and the header (process-crash safe once they are in the OS cache), then its durability thread flushes to
    /// disk (coalesced with a flush that is still running). For a stream: <see cref="UpdateHeader"/> +
    /// <see cref="Stream.Flush()"/> (a <see cref="FileStream"/> is not forced to disk; see <see cref="FlushToDisk"/>).
    /// </summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_behind is not null)
        {
            _behind.RequestDurableFlush(); // the I/O thread rewrites the header before every flush to disk
            _headerDataBytes = _dataBytes;
            return;
        }

        UpdateHeader();
        if (_stream is FileStream file)
        {
            file.Flush(flushToDisk: false);
        }
        else
        {
            _stream!.Flush();
        }
    }

    /// <summary>
    /// Writes whatever is still queued, rewrites the header, flushes to disk (blocking) and closes the file.
    /// </summary>
    /// <exception cref="IOException">Writing failed (now or earlier on the background I/O thread).</exception>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_behind is not null)
            {
                _behind.Dispose();
            }
            else
            {
                UpdateHeader();
                FlushToDisk();
            }
        }
        finally
        {
            _disposed = true;
            _handle?.Dispose();
            if (!_leaveOpen)
            {
                _stream?.Dispose();
            }
        }
    }

    /// <summary>Writes a canonical 44-byte PCM16 header.</summary>
    public static void WriteHeader(Span<byte> header, int sampleRate, int channels, long dataBytes)
    {
        if (header.Length < HeaderSize)
        {
            throw new ArgumentException("Header buffer must hold 44 bytes.", nameof(header));
        }

        var data = (uint)Math.Clamp(dataBytes, 0, uint.MaxValue - 36);
        var blockAlign = channels * 2;
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], data + 36);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1); // WAVE_FORMAT_PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], data);
    }

    /// <summary>Float to little-endian PCM16 with clipping; NaN becomes 0.</summary>
    internal static void Convert(ReadOnlySpan<float> samples, Span<byte> destination)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i];
            short value;
            if (x >= 1f)
            {
                value = short.MaxValue;
            }
            else if (x <= -1f)
            {
                value = -short.MaxValue;
            }
            else
            {
                // NaN fails both comparisons above and converts to 0 (saturating conversion).
                value = (short)MathF.Round(x * short.MaxValue);
            }

            BinaryPrimitives.WriteInt16LittleEndian(destination[(2 * i)..], value);
        }
    }

    private static void Validate(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, ushort.MaxValue / 2);
    }

    private void WriteInitialHeader()
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        WriteHeader(header, SampleRate, Channels, dataBytes: 0);
        WriteAt(0, header);
        _headerDataBytes = 0;

        // Once, at construction, before any real-time thread uses the writer.
        if (_handle is not null)
        {
            RandomAccess.FlushToDisk(_handle);
        }
        else if (_stream is FileStream file)
        {
            file.Flush(flushToDisk: true);
        }
        else
        {
            _stream!.Flush();
        }
    }

    private void WriteAt(long offset, ReadOnlySpan<byte> bytes)
    {
        if (_handle is not null)
        {
            RandomAccess.Write(_handle, bytes, offset);
        }
        else
        {
            _stream!.Position = offset;
            _stream.Write(bytes);
        }
    }
}
