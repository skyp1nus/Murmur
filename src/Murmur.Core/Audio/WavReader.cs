using System.Buffers.Binary;
using Murmur.Core.Capture;

namespace Murmur.Core.Audio;

/// <summary>
/// Streams interleaved float samples out of a PCM (8/16/24/32-bit) or IEEE float (32/64-bit) WAV of any rate and
/// channel count, including <c>WAVE_FORMAT_EXTENSIBLE</c>. A stale data size (crashed writer) is tolerated: the frame
/// count is the smaller of the declared size and what is physically in the file, and a zero size means "to the end".
/// </summary>
public sealed class WavReader : IDisposable
{
    private const int ChunkBytes = 64 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _bytes;
    private long _framesLeft;

    private WavReader(Stream stream, WavLayout layout)
    {
        _stream = stream;
        Layout = layout;
        var physical = layout.FramesInFile(stream.Length);
        var declared = layout.DeclaredDataBytes / layout.BlockAlign;
        FrameCount = declared == 0 ? physical : Math.Min(declared, physical);
        _framesLeft = FrameCount;
        _bytes = new byte[Math.Max(layout.BlockAlign, ChunkBytes / layout.BlockAlign * layout.BlockAlign)];
        _stream.Position = layout.DataOffset;
    }

    public WavLayout Layout { get; }

    public AudioFormat Format => new(Layout.SampleRate, Layout.Channels);

    public long FrameCount { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / Layout.SampleRate);

    /// <exception cref="InvalidDataException">The file is not a supported WAV.</exception>
    public static WavReader Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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

    /// <summary>Takes ownership of a seekable <paramref name="stream"/>.</summary>
    /// <exception cref="InvalidDataException">The stream is not a supported WAV.</exception>
    public static WavReader Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!WavLayout.TryRead(stream, out var layout))
        {
            throw new InvalidDataException("Not a PCM or IEEE-float RIFF/WAVE stream.");
        }

        return new WavReader(stream, layout);
    }

    /// <summary>Reads a whole file into memory.</summary>
    public static (AudioFormat Format, float[] Samples) ReadAll(string path)
    {
        using var reader = Open(path);
        var samples = new float[checked((int)(reader.FrameCount * reader.Layout.Channels))];
        var offset = 0;
        int frames;
        while ((frames = reader.Read(samples.AsSpan(offset))) > 0)
        {
            offset += frames * reader.Layout.Channels;
        }

        return (reader.Format, samples);
    }

    /// <summary>Reads up to <c>destination.Length / Channels</c> frames of interleaved samples in [-1, 1].</summary>
    /// <returns>Frames read; 0 at the end.</returns>
    public int Read(Span<float> destination)
    {
        var channels = Layout.Channels;
        var bytesPerSample = Layout.BitsPerSample / 8;
        var total = 0;

        while (_framesLeft > 0)
        {
            var frames = (int)Math.Min(Math.Min(_framesLeft, (destination.Length / channels) - total), _bytes.Length / Layout.BlockAlign);
            if (frames <= 0)
            {
                break;
            }

            var bytes = _bytes.AsSpan(0, frames * Layout.BlockAlign);
            _stream.ReadExactly(bytes);
            Decode(bytes, destination.Slice(total * channels, frames * channels), bytesPerSample);
            total += frames;
            _framesLeft -= frames;
        }

        return total;
    }

    public void Dispose() => _stream.Dispose();

    private void Decode(ReadOnlySpan<byte> bytes, Span<float> samples, int bytesPerSample)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var b = bytes[(i * bytesPerSample)..];
            samples[i] = (Layout.Encoding, bytesPerSample) switch
            {
                (WavSampleEncoding.Pcm, 1) => (b[0] - 128) / 128f,
                (WavSampleEncoding.Pcm, 2) => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
                (WavSampleEncoding.Pcm, 3) => ((b[0] << 8 | b[1] << 16 | b[2] << 24) >> 8) / 8388608f,
                (WavSampleEncoding.Pcm, 4) => (float)(BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648.0),
                (WavSampleEncoding.Float, 4) => BinaryPrimitives.ReadSingleLittleEndian(b),
                (WavSampleEncoding.Float, 8) => (float)BinaryPrimitives.ReadDoubleLittleEndian(b),
                _ => 0f,
            };
        }
    }
}
