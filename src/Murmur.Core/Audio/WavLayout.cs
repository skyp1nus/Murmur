using System.Buffers.Binary;

namespace Murmur.Core.Audio;

/// <summary>Sample encodings a WAV data chunk can hold.</summary>
public enum WavSampleEncoding
{
    Pcm,
    Float,
}

/// <summary>Where the audio lives inside a RIFF/WAVE file and how it is encoded.</summary>
/// <param name="DataOffset">Byte offset of the first sample.</param>
/// <param name="DeclaredDataBytes">Data size from the chunk header (may be stale after a crash).</param>
public readonly record struct WavLayout(
    WavSampleEncoding Encoding,
    int Channels,
    int SampleRate,
    int BitsPerSample,
    int BlockAlign,
    long DataOffset,
    long DeclaredDataBytes)
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>
    /// Most chunks looked at before the data chunk. Chunks are skipped by their declared size (a seek, never a read), so
    /// the size of a chunk (cover art in LIST/id3, iXML...) does not matter, only how many there are.
    /// </summary>
    internal const int MaxChunksBeforeData = 4096;

    /// <summary>Parses the RIFF header and chunk list up to the data chunk.</summary>
    public static bool TryRead(Stream stream, out WavLayout layout)
    {
        layout = default;
        stream.Position = 0;
        Span<byte> riff = stackalloc byte[12];
        if (!TryReadExactly(stream, riff) || !riff[..4].SequenceEqual("RIFF"u8) || !riff[8..12].SequenceEqual("WAVE"u8))
        {
            return false;
        }

        Span<byte> chunk = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[40];
        var haveFormat = false;
        ushort tag = 0;
        int channels = 0, sampleRate = 0, blockAlign = 0, bits = 0;

        for (var chunks = 0; chunks < MaxChunksBeforeData && TryReadExactly(stream, chunk); chunks++)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (size < 16)
                {
                    return false;
                }

                var take = (int)Math.Min(size, (uint)fmt.Length);
                if (!TryReadExactly(stream, fmt[..take]))
                {
                    return false;
                }

                tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                if (tag == FormatExtensible && take >= 26)
                {
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]); // first two bytes of the sub-format GUID
                }

                haveFormat = true;
                if (!TrySkip(stream, (long)(size - take) + (size & 1)))
                {
                    return false;
                }
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (!haveFormat)
                {
                    return false;
                }

                var encoding = tag switch
                {
                    FormatPcm when bits is 8 or 16 or 24 or 32 => WavSampleEncoding.Pcm,
                    FormatFloat when bits is 32 or 64 => WavSampleEncoding.Float,
                    _ => (WavSampleEncoding?)null,
                };

                if (encoding is null || channels < 1 || sampleRate < 1 || blockAlign != channels * (bits / 8))
                {
                    return false;
                }

                layout = new WavLayout(encoding.Value, channels, sampleRate, bits, blockAlign, stream.Position, size);
                return true;
            }
            else if (!TrySkip(stream, (long)size + (size & 1)))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Moves the stream forward by <paramref name="bytes"/> (a seek, nothing is read). Overflow-safe (the size is a 32-bit
    /// value from the file, the sum is made in 64 bits); <c>false</c> when that would go past the end of the stream, i.e. the
    /// chunk is cut off.
    /// </summary>
    internal static bool TrySkip(Stream stream, long bytes)
    {
        var position = stream.Position;
        if (bytes < 0 || bytes > stream.Length - position)
        {
            return false;
        }

        stream.Position = position + bytes;
        return true;
    }

    /// <summary>Whole frames that are physically present in a file of <paramref name="fileLength"/> bytes.</summary>
    public long FramesInFile(long fileLength) => Math.Max(0, fileLength - DataOffset) / BlockAlign;

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        try
        {
            stream.ReadExactly(buffer);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}
