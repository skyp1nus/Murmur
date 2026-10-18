using System.Buffers;
using System.Buffers.Binary;

namespace Murmur.Core.Audio;

/// <summary>
/// <c>peaks.bin</c> (<see cref="Sessions.SessionFiles.Peaks"/>): the waveform overview of a session.
/// Layout (little-endian): magic <c>"MPK1"</c>, int32 peaks per second (50), int32 count, then <c>count</c> bytes;
/// byte <c>i</c> is the largest |sample| of every track in window <c>[i / 50 s, (i + 1) / 50 s)</c>, scaled to 0..255.
/// </summary>
public static class PeaksFile
{
    public const int PeaksPerSecond = 50;

    public const int HeaderSize = 12;

    public static ReadOnlySpan<byte> Magic => "MPK1"u8;

    /// <summary>Reads the peak bytes of a <c>peaks.bin</c>.</summary>
    /// <exception cref="InvalidDataException">The file is not a peaks file or is truncated.</exception>
    public static byte[] Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(stream);
    }

    /// <inheritdoc cref="Read(string)"/>
    public static byte[] Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[HeaderSize];
        try
        {
            stream.ReadExactly(header);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The peaks file is truncated.", ex);
        }

        if (!header[..4].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a peaks file (magic MPK1 expected).");
        }

        var perSecond = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (perSecond != PeaksPerSecond || count < 0)
        {
            throw new InvalidDataException($"Unsupported peaks file: {perSecond} peaks/s, {count} peaks.");
        }

        var peaks = new byte[count];
        try
        {
            stream.ReadExactly(peaks);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The peaks file is truncated.", ex);
        }

        return peaks;
    }

    /// <summary>Writes a complete peaks file (header + <paramref name="peaks"/>).</summary>
    public static void Write(Stream stream, ReadOnlySpan<byte> peaks)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], PeaksPerSecond);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], peaks.Length);
        stream.Write(header);
        stream.Write(peaks);
    }
}

/// <summary>Builds <see cref="PeaksFile"/> data from session WAVs, streaming each file in chunks.</summary>
public static class PeaksBuilder
{
    private const int ChunkWindows = 256;

    /// <summary>Computes the peaks of <paramref name="wavPaths"/> and writes them atomically to <paramref name="outputPath"/>.</summary>
    /// <returns>The number of peaks written.</returns>
    /// <exception cref="InvalidDataException">A file is not a supported WAV.</exception>
    public static int Build(IReadOnlyList<string> wavPaths, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var peaks = Compute(wavPaths, cancellationToken);
        var temp = outputPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            PeaksFile.Write(stream, peaks);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, outputPath, overwrite: true);
        return peaks.Length;
    }

    /// <summary>One byte per 1/50 s window: max |sample| over every track (and channel), scaled to 0..255.</summary>
    /// <exception cref="InvalidDataException">A file is not a supported WAV.</exception>
    public static byte[] Compute(IReadOnlyList<string> wavPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wavPaths);
        var readers = new List<WavReader>(wavPaths.Count);
        try
        {
            var count = 0L;
            foreach (var path in wavPaths)
            {
                var reader = WavReader.Open(path);
                readers.Add(reader);
                count = Math.Max(count, WindowCount(reader.FrameCount, WindowFrames(reader.Layout.SampleRate)));
            }

            var peaks = new byte[checked((int)count)];
            foreach (var reader in readers)
            {
                Accumulate(reader, peaks, cancellationToken);
            }

            return peaks;
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    /// <summary>Scales a peak in [0, 1] (clamped) to 0..255.</summary>
    public static byte ToByte(float peak) => (byte)Math.Clamp((int)((peak * 255f) + 0.5f), 0, 255);

    private static int WindowFrames(int sampleRate) => Math.Max(1, sampleRate / PeaksFile.PeaksPerSecond);

    private static long WindowCount(long frames, int windowFrames) => (frames + windowFrames - 1) / windowFrames;

    private static void Accumulate(WavReader reader, byte[] peaks, CancellationToken cancellationToken)
    {
        var channels = reader.Layout.Channels;
        var windowFrames = WindowFrames(reader.Layout.SampleRate);
        var windowSamples = windowFrames * channels;
        var buffer = ArrayPool<float>.Shared.Rent(windowSamples * ChunkWindows);
        try
        {
            var chunk = buffer.AsSpan(0, windowSamples * ChunkWindows);
            var window = 0;
            var filled = 0; // samples of the current, partially read window at the start of chunk
            int frames;
            while ((frames = reader.Read(chunk[filled..])) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var available = filled + (frames * channels);
                var offset = 0;
                while (available - offset >= windowSamples)
                {
                    Merge(peaks, window++, chunk.Slice(offset, windowSamples));
                    offset += windowSamples;
                }

                filled = available - offset;
                chunk.Slice(offset, filled).CopyTo(chunk);
            }

            if (filled > 0)
            {
                Merge(peaks, window, chunk[..filled]);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    private static void Merge(byte[] peaks, int window, ReadOnlySpan<float> samples)
    {
        var max = 0f;
        foreach (var s in samples)
        {
            var a = Math.Abs(s);
            if (a > max)
            {
                max = a;
            }
        }

        var value = ToByte(max);
        if (value > peaks[window])
        {
            peaks[window] = value;
        }
    }
}
