using System.Runtime.InteropServices;
using Murmur.Core.Audio;

namespace Murmur.Capture.Windows.Playback;

/// <summary>
/// Seekable, chunked reader of one mono PCM16 WAV track (the session format: 16 kHz mono PCM16). The file is never
/// loaded whole: <see cref="Read"/> pulls exactly the requested frames straight into the caller's span (the
/// <see cref="FileStream"/> keeps its own fixed read-ahead buffer), so steady-state reads allocate nothing.
/// A stale data size (crashed writer) is tolerated like <see cref="WavReader"/>: the frame count is the smaller of the
/// declared size and what is physically in the file, and a zero size means "to the end".
/// Not thread-safe: owned by one render thread at a time.
/// </summary>
internal sealed class Pcm16TrackReader : IDisposable
{
    /// <summary>Read-ahead of the underlying <see cref="FileStream"/>: 2 s of 16 kHz mono PCM16.</summary>
    internal const int FileBufferBytes = 64 * 1024;

    private readonly Stream _stream;
    private readonly long _dataOffset;
    private long _position;

    private Pcm16TrackReader(Stream stream, WavLayout layout)
    {
        _stream = stream;
        _dataOffset = layout.DataOffset;
        SampleRate = layout.SampleRate;
        var physical = layout.FramesInFile(stream.Length);
        var declared = layout.DeclaredDataBytes / layout.BlockAlign;
        FrameCount = declared == 0 ? physical : Math.Min(declared, physical);
        _stream.Position = _dataOffset;
    }

    public int SampleRate { get; }

    public long FrameCount { get; }

    /// <summary>The next frame <see cref="Read"/> returns, in [0, <see cref="FrameCount"/>].</summary>
    public long Position => _position;

    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not a mono PCM16 WAV.</exception>
    public static Pcm16TrackReader Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Audio track not found.", path);
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileBufferBytes);
        try
        {
            return Open(stream, path);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Takes ownership of a seekable <paramref name="stream"/>.</summary>
    /// <exception cref="InvalidDataException">The stream is not a mono PCM16 WAV.</exception>
    public static Pcm16TrackReader Open(Stream stream, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("PCM16 playback reads samples in place and needs a little-endian CPU.");
        }

        if (!WavLayout.TryRead(stream, out var layout))
        {
            throw new InvalidDataException($"Not a PCM or IEEE-float RIFF/WAVE file: {name ?? "stream"}.");
        }

        if (layout.Encoding != WavSampleEncoding.Pcm || layout.BitsPerSample != 16 || layout.Channels != 1)
        {
            throw new InvalidDataException(
                $"Playback needs mono 16-bit PCM; {name ?? "stream"} is {layout.Channels} ch {layout.BitsPerSample}-bit {layout.Encoding}.");
        }

        return new Pcm16TrackReader(stream, layout);
    }

    /// <summary>Moves to <paramref name="frame"/>, clamped to [0, <see cref="FrameCount"/>].</summary>
    public void Seek(long frame)
    {
        _position = Math.Clamp(frame, 0, FrameCount);
        _stream.Position = _dataOffset + (_position * sizeof(short));
    }

    /// <summary>Reads up to <c>destination.Length</c> samples; fewer only at the end of the track.</summary>
    /// <returns>Frames read; 0 at the end.</returns>
    /// <exception cref="EndOfStreamException">The file was truncated while it was being played.</exception>
    public int Read(Span<short> destination)
    {
        var frames = (int)Math.Min(destination.Length, FrameCount - _position);
        if (frames <= 0)
        {
            return 0;
        }

        _stream.ReadExactly(MemoryMarshal.AsBytes(destination[..frames]));
        _position += frames;
        return frames;
    }

    public void Dispose() => _stream.Dispose();
}
