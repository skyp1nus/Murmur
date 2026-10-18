using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Murmur.Transcription.Audio;

/// <summary>A forward-only stream of 16 kHz mono float samples in [-1, 1].</summary>
internal interface ISampleSource : IDisposable
{
    /// <summary>Total number of samples the source will produce.</summary>
    long TotalSamples { get; }

    /// <summary>Fills <paramref name="destination"/> as far as possible; returns 0 only at the end.</summary>
    int Read(Span<float> destination);
}

/// <summary>
/// Streams the samples of a 16 kHz mono WAV file (PCM16 or IEEE float32, plain or WAVE_FORMAT_EXTENSIBLE) chunk by
/// chunk, so an hour-long recording is never loaded at once. Anything else is rejected when the file is opened.
/// </summary>
internal sealed class WavReader : ISampleSource
{
    public const int RequiredSampleRate = 16_000;

    private const int ReadBlockBytes = 64 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _bytes;
    private readonly int _bytesPerSample;
    private long _remainingBytes;
    private int _pending; // bytes of an incomplete sample kept at the start of _bytes

    private WavReader(Stream stream, WavHeader header)
    {
        _stream = stream;
        Header = header;
        _bytesPerSample = header.BitsPerSample / 8;
        _remainingBytes = header.DataLength;
        _bytes = ArrayPool<byte>.Shared.Rent(ReadBlockBytes);
    }

    public WavHeader Header { get; }

    public long TotalSamples => Header.Frames;

    public double DurationSeconds => Header.DurationSeconds;

    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">Not a WAV file, or not 16 kHz mono PCM16/float32.</exception>
    public static WavReader Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        try
        {
            return Open(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Takes ownership of <paramref name="stream"/>.</summary>
    public static WavReader Open(Stream stream)
    {
        var header = WavHeader.Read(stream);
        Validate(header);
        return new WavReader(stream, header);
    }

    public static void Validate(WavHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var supported = header.SampleRate == RequiredSampleRate && header.Channels == 1
            && ((header.Encoding == WavEncoding.Pcm && header.BitsPerSample == 16)
                || (header.Encoding == WavEncoding.IeeeFloat && header.BitsPerSample == 32));
        if (!supported)
        {
            throw new InvalidDataException(
                $"Unsupported WAV format: {header.Describe()}. The ASR worker needs 16000 Hz mono PCM16 or float32.");
        }
    }

    public int Read(Span<float> destination)
    {
        var produced = 0;
        while (produced < destination.Length && (_remainingBytes > 0 || _pending >= _bytesPerSample))
        {
            var wantSamples = destination.Length - produced;
            var wantBytes = (int)Math.Min(Math.Min((long)wantSamples * _bytesPerSample, _bytes.Length) - _pending, _remainingBytes);
            var read = wantBytes > 0 ? _stream.Read(_bytes, _pending, wantBytes) : 0;
            if (read == 0 && _pending < _bytesPerSample)
            {
                _remainingBytes = 0; // file shorter than its header claims
                break;
            }

            _remainingBytes -= read;
            var available = _pending + read;
            var samples = available / _bytesPerSample;
            Convert(_bytes.AsSpan(0, samples * _bytesPerSample), destination.Slice(produced, samples));
            produced += samples;

            var used = samples * _bytesPerSample;
            _pending = available - used;
            if (_pending > 0)
            {
                _bytes.AsSpan(used, _pending).CopyTo(_bytes);
            }
        }

        return produced;
    }

    private void Convert(ReadOnlySpan<byte> src, Span<float> dst)
    {
        if (Header.Encoding == WavEncoding.IeeeFloat)
        {
            if (BitConverter.IsLittleEndian)
            {
                MemoryMarshal.Cast<byte, float>(src).CopyTo(dst);
                return;
            }

            for (var i = 0; i < dst.Length; i++)
            {
                dst[i] = BinaryPrimitives.ReadSingleLittleEndian(src[(i * 4)..]);
            }

            return;
        }

        PcmConvert.Int16ToFloat(src, dst);
    }

    public void Dispose()
    {
        _stream.Dispose();
        ArrayPool<byte>.Shared.Return(_bytes);
    }
}

internal static class PcmConvert
{
    private const float Int16Scale = 1f / 32768f;

    /// <summary>Little-endian PCM16 bytes -> floats in [-1, 1).</summary>
    public static void Int16ToFloat(ReadOnlySpan<byte> src, Span<float> dst)
    {
        var count = Math.Min(src.Length / 2, dst.Length);
        if (BitConverter.IsLittleEndian)
        {
            var s = MemoryMarshal.Cast<byte, short>(src[..(count * 2)]);
            for (var i = 0; i < s.Length; i++)
            {
                dst[i] = s[i] * Int16Scale;
            }

            return;
        }

        for (var i = 0; i < count; i++)
        {
            dst[i] = BinaryPrimitives.ReadInt16LittleEndian(src[(i * 2)..]) * Int16Scale;
        }
    }
}
