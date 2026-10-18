using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Murmur.Core.Audio;

/// <summary>
/// Crash-safe 16-bit PCM WAV writer (canonical 44-byte header, 16 kHz mono by default).
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="Append"/> converts into a preallocated byte buffer and hands the bytes straight to the OS with a
/// positional write (no user-space buffering), so a killed process loses nothing that was appended.
/// <see cref="Flush"/> = <see cref="UpdateHeader"/> (rewrite the RIFF and data sizes) + <see cref="FlushToDisk"/>
/// (fsync / FlushFileBuffers). Between header updates the sizes lag behind; <see cref="WavRepair.TryRepair"/> fixes
/// them after a crash. Nothing here allocates after construction.
/// </para>
/// <para>
/// Threading: one thread appends and updates the header. For a file-backed writer <see cref="FlushToDisk"/> may run
/// concurrently on another thread (so a slow disk flush never blocks the appending thread); <see cref="Dispose"/>
/// must not race with anything.
/// </para>
/// </remarks>
public sealed class WavPcm16Writer : IDisposable
{
    /// <summary>Size of the canonical PCM header written by this class.</summary>
    public const int HeaderSize = 44;

    private const int ConvertChunkSamples = 4096;

    private readonly SafeFileHandle? _handle;
    private readonly Stream? _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _bytes = new byte[ConvertChunkSamples * 2];
    private readonly byte[] _sizeField = new byte[4];
    private long _dataBytes;
    private long _headerDataBytes = -1;
    private volatile bool _disposed;

    /// <summary>Creates (or truncates) <paramref name="path"/>, writes the header with zero sizes and flushes it to disk.</summary>
    public WavPcm16Writer(string path, int sampleRate = 16_000, int channels = 1)
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

    /// <summary>Converts float samples in [-1, 1] (clipped; NaN = 0) to PCM16 and appends them.</summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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

    /// <summary>Rewrites the RIFF and data sizes to match what was appended (cheap; no flush to disk).</summary>
    public void UpdateHeader()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_headerDataBytes == _dataBytes)
        {
            return;
        }

        var data = (uint)Math.Min(_dataBytes, uint.MaxValue - 36);
        BinaryPrimitives.WriteUInt32LittleEndian(_sizeField, data + 36);
        WriteAt(4, _sizeField);
        BinaryPrimitives.WriteUInt32LittleEndian(_sizeField, data);
        WriteAt(40, _sizeField);
        _headerDataBytes = _dataBytes;
    }

    /// <summary>Forces everything written so far to the storage device. May block for a long time on a busy disk.</summary>
    public void FlushToDisk()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    /// <summary><see cref="UpdateHeader"/> + <see cref="FlushToDisk"/>.</summary>
    public void Flush()
    {
        UpdateHeader();
        FlushToDisk();
    }

    /// <summary>Flushes and closes the file.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Flush();
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
        FlushToDisk();
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
