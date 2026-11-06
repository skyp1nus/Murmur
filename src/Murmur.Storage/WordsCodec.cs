using System.Buffers.Binary;
using System.Text;
using Murmur.Core.Transcription;

namespace Murmur.Storage;

/// <summary>
/// Compact little-endian binary form of a segment's words. No reflection, no allocations besides the result.
/// <list type="bullet">
/// <item><b>v1</b>: <c>count:int32</c>, then per word <c>start_ms:int32, end_ms:int32, utf8Length:uint16, utf8 bytes</c>.</item>
/// <item><b>v2</b> (D-052): <c>-2:int32</c>, then the v1 layout with one more byte after each word's text: its confidence,
/// <c>round(confidence × 254)</c>, or 255 when it is unknown.</item>
/// </list>
/// A blob starts with the word count (never negative) in v1 and with the marker in v2, so both are read. <see cref="Encode"/> writes v2
/// only when a word has a confidence: the words of older engines (and of transcripts whose every word was confirmed) stay in v1, which an
/// older build still reads.
/// </summary>
internal static class WordsCodec
{
    /// <summary>The first int32 of a v2 blob.</summary>
    internal const int Version2Marker = -2;

    /// <summary>The confidence byte of a word whose confidence is unknown.</summary>
    internal const byte UnknownConfidence = 255;

    private const int WordHeaderSize = sizeof(int) + sizeof(int) + sizeof(ushort);
    private const float ConfidenceScale = 254f;

    public static byte[] Encode(IReadOnlyList<TranscriptWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        var withConfidence = false;
        var size = sizeof(int);
        for (var i = 0; i < words.Count; i++)
        {
            var length = Encoding.UTF8.GetByteCount(words[i].Text);
            if (length > ushort.MaxValue)
            {
                throw new ArgumentException($"Word {i} is longer than {ushort.MaxValue} UTF-8 bytes.", nameof(words));
            }

            size += WordHeaderSize + length;
            withConfidence |= words[i].Confidence is not null;
        }

        if (withConfidence)
        {
            // The marker, then one confidence byte per word.
            size += sizeof(int) + words.Count;
        }

        var buffer = new byte[size];
        var span = buffer.AsSpan();
        var offset = 0;
        if (withConfidence)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span, Version2Marker);
            offset = sizeof(int);
        }

        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], words.Count);
        offset += sizeof(int);
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            BinaryPrimitives.WriteInt32LittleEndian(span[offset..], ToMilliseconds(word.Start));
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 4)..], ToMilliseconds(word.End));
            var written = Encoding.UTF8.GetBytes(word.Text, span[(offset + WordHeaderSize)..]);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 8)..], (ushort)written);
            offset += WordHeaderSize + written;
            if (withConfidence)
            {
                span[offset++] = ToByte(word.Confidence);
            }
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

        var withConfidence = false;
        if (BinaryPrimitives.ReadInt32LittleEndian(data) == Version2Marker)
        {
            withConfidence = true;
            data = data[sizeof(int)..];
            if (data.Length < sizeof(int))
            {
                throw new InvalidDataException("Words blob is truncated.");
            }
        }

        var perWord = WordHeaderSize + (withConfidence ? 1 : 0);
        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (count < 0 || (long)count * perWord > data.Length - sizeof(int))
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
            if (data.Length - offset < length + (withConfidence ? 1 : 0))
            {
                throw new InvalidDataException("Words blob is truncated.");
            }

            var text = Encoding.UTF8.GetString(data.Slice(offset, length));
            offset += length;
            float? confidence = null;
            if (withConfidence)
            {
                confidence = FromByte(data[offset++]);
            }

            words[i] = new TranscriptWord(text, TimeSpan.FromMilliseconds(start), TimeSpan.FromMilliseconds(end)) { Confidence = confidence };
        }

        if (offset != data.Length)
        {
            throw new InvalidDataException("Words blob has trailing bytes.");
        }

        return words;
    }

    /// <summary>0..1 in 254 steps (out-of-range values are clamped, NaN is unknown); 255 = unknown.</summary>
    internal static byte ToByte(float? confidence) =>
        confidence is { } value && !float.IsNaN(value)
            ? (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * ConfidenceScale)
            : UnknownConfidence;

    internal static float? FromByte(byte value) => value == UnknownConfidence ? null : value / ConfidenceScale;

    private static int ToMilliseconds(TimeSpan value) => checked((int)Math.Round(value.TotalMilliseconds));
}
