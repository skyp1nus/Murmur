using System.Buffers.Binary;

namespace Murmur.Transcription.Audio;

internal enum WavEncoding
{
    Pcm,
    IeeeFloat,
}

/// <summary>Parsed RIFF/WAVE header: format plus the location of the <c>data</c> chunk.</summary>
internal sealed record WavHeader(WavEncoding Encoding, int SampleRate, int Channels, int BitsPerSample, int BlockAlign, long DataOffset, long DataLength)
{
    private const ushort FormatPcm = 0x0001;
    private const ushort FormatIeeeFloat = 0x0003;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>Whole frames in the data chunk.</summary>
    public long Frames => DataLength / BlockAlign;

    public double DurationSeconds => (double)Frames / SampleRate;

    public string Describe() =>
        $"{SampleRate} Hz, {Channels} ch, {(Encoding == WavEncoding.IeeeFloat ? "float" : "PCM")}{BitsPerSample}";

    /// <summary>
    /// Reads chunks from the start of <paramref name="stream"/> up to the beginning of the <c>data</c> chunk and leaves
    /// the stream positioned at the first sample. A data size of 0 or 0xFFFFFFFF, or one running past the end of the
    /// file (a recording whose header was never finalised, e.g. after a crash), means "until end of file".
    /// </summary>
    /// <exception cref="InvalidDataException">Not a RIFF/WAVE file, or the header is inconsistent.</exception>
    public static WavHeader Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> head = stackalloc byte[12];
        if (!TryReadExactly(stream, head) || !head[..4].SequenceEqual("RIFF"u8) || !head.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        ushort format = 0;
        int channels = 0, sampleRate = 0, bits = 0, blockAlign = 0;
        var haveFmt = false;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[40];

        while (true)
        {
            if (!TryReadExactly(stream, chunk))
            {
                throw new InvalidDataException(haveFmt ? "WAV file has no data chunk." : "WAV file has no fmt chunk.");
            }

            var id = chunk[..4];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);

            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16 || size > 1024)
                {
                    throw new InvalidDataException($"Invalid fmt chunk size {size}.");
                }

                var keep = (int)Math.Min(size, (uint)fmt.Length);
                if (!TryReadExactly(stream, fmt[..keep]))
                {
                    throw new InvalidDataException("Truncated fmt chunk.");
                }

                Skip(stream, size - (uint)keep + (size & 1));
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                if (format == FormatExtensible)
                {
                    if (keep < 40)
                    {
                        throw new InvalidDataException("WAVE_FORMAT_EXTENSIBLE fmt chunk too short.");
                    }

                    // SubFormat GUID at offset 24; its first two bytes are the underlying format tag
                    // (KSDATAFORMAT_SUBTYPE_PCM = 00000001-..., KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = 00000003-...).
                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
                }

                haveFmt = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (!haveFmt)
                {
                    throw new InvalidDataException("WAV data chunk precedes the fmt chunk.");
                }

                var encoding = format switch
                {
                    FormatPcm => WavEncoding.Pcm,
                    FormatIeeeFloat => WavEncoding.IeeeFloat,
                    _ => throw new InvalidDataException($"Unsupported WAV format tag 0x{format:X4}."),
                };
                if (channels <= 0 || sampleRate <= 0 || bits <= 0 || bits % 8 != 0 || blockAlign != channels * (bits / 8))
                {
                    throw new InvalidDataException($"Inconsistent WAV header: channels={channels}, rate={sampleRate}, bits={bits}, blockAlign={blockAlign}.");
                }

                var offset = stream.CanSeek ? stream.Position : -1;
                long length = size;
                if (stream.CanSeek && (size == 0 || size == uint.MaxValue || offset + size > stream.Length))
                {
                    length = stream.Length - offset;
                }

                length -= length % blockAlign;
                return new WavHeader(encoding, sampleRate, channels, bits, blockAlign, offset, length);
            }
            else
            {
                Skip(stream, size + (size & 1)); // chunks are word-aligned
            }
        }
    }

    private static void Skip(Stream stream, long count)
    {
        if (count == 0)
        {
            return;
        }

        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        Span<byte> scratch = stackalloc byte[256];
        while (count > 0)
        {
            var n = stream.Read(scratch[..(int)Math.Min(count, scratch.Length)]);
            if (n == 0)
            {
                return;
            }

            count -= n;
        }
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer) =>
        stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
}
