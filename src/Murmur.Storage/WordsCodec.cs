using System.Buffers.Binary;
using System.Text;
using Murmur.Core.Transcription;

namespace Murmur.Storage;

/// <summary>
/// Compact little-endian binary form of a segment's words: <c>count:int32</c>, then per word
/// <c>start_ms:int32, end_ms:int32, utf8Length:uint16, utf8 bytes</c>. No reflection, no allocations besides the result.
/// </summary>
internal static class WordsCodec
{
    private const int WordHeaderSize = sizeof(int) + sizeof(int) + sizeof(ushort);

    public static byte[] Encode(IReadOnlyList<TranscriptWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        var size = sizeof(int);
        for (var i = 0; i < words.Count; i++)
        {
            var length = Encoding.UTF8.GetByteCount(words[i].Text);
            if (length > ushort.MaxValue)
            {
                throw new ArgumentException($"Word {i} is longer than {ushort.MaxValue} UTF-8 bytes.", nameof(words));
            }

            size += WordHeaderSize + length;
        }

        var buffer = new byte[size];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, words.Count);
        var offset = sizeof(int);
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            BinaryPrimitives.WriteInt32LittleEndian(span[offset..], ToMilliseconds(word.Start));
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 4)..], ToMilliseconds(word.End));
            var written = Encoding.UTF8.GetBytes(word.Text, span[(offset + WordHeaderSize)..]);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 8)..], (ushort)written);
            offset += WordHeaderSize + written;
        }

        return buffer;
    }

    /// <exception cref="InvalidDataException">The blob is truncated or inconsistent.</exception>
    public static TranscriptWord[] Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return [];
        }

        if (data.Length < sizeof(int))
        {
            throw new InvalidDataException("Words blob is truncated.");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (count < 0 || (long)count * WordHeaderSize > data.Length - sizeof(int))
        {
            throw new InvalidDataException("Words blob has an invalid word count.");
        }

        var words = new TranscriptWord[count];
        var offset = sizeof(int);
        for (var i = 0; i < count; i++)
        {
            if (data.Length - offset < WordHeaderSize)
            {
                throw new InvalidDataException("Words blob is truncated.");
            }

            var start = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
            var end = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 8)..]);
            offset += WordHeaderSize;
            if (data.Length - offset < length)
            {
                throw new InvalidDataException("Words blob is truncated.");
            }

            words[i] = new TranscriptWord(
                Encoding.UTF8.GetString(data.Slice(offset, length)),
                TimeSpan.FromMilliseconds(start),
                TimeSpan.FromMilliseconds(end));
            offset += length;
        }

        if (offset != data.Length)
        {
            throw new InvalidDataException("Words blob has trailing bytes.");
        }

        return words;
    }

    private static int ToMilliseconds(TimeSpan value) => checked((int)Math.Round(value.TotalMilliseconds));
}
