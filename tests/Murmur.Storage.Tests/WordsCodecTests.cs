using System.Buffers.Binary;
using Murmur.Core.Transcription;

namespace Murmur.Storage.Tests;

public sealed class WordsCodecTests
{
    private static TranscriptWord W(string text, int start, int end) =>
        new(text, TimeSpan.FromMilliseconds(start), TimeSpan.FromMilliseconds(end));

    [Fact]
    public void Roundtrip_PreservesTextAndTimes_IncludingCyrillicAndEmoji()
    {
        TranscriptWord[] words = [W("hello", 0, 400), W("бюджет", 400, 900), W("🎉", 900, 1200), W("Straße", 1200, 1500)];

        var decoded = WordsCodec.Decode(WordsCodec.Encode(words));

        Assert.Equal(words, decoded);
    }

    [Fact]
    public void WordsWithoutConfidence_AreStoredInV1_ByteIdenticalToBefore()
    {
        // An older build reads them; only a word with a confidence makes the blob v2 (D-052).
        TranscriptWord[] words = [W("hello", 0, 400), W("world", 400, 900)];

        var blob = WordsCodec.Encode(words);

        Assert.Equal(words.Length, BinaryPrimitives.ReadInt32LittleEndian(blob));
        Assert.Equal(sizeof(int) + (2 * 10) + 5 + 5, blob.Length);
        Assert.All(WordsCodec.Decode(blob), w => Assert.Null(w.Confidence));
    }

    [Fact]
    public void V2_RoundtripsConfidence_IncludingNullZeroAndOne()
    {
        TranscriptWord[] words =
        [
            W("hello", 0, 400) with { Confidence = 0.42f },
            W("бюджет", 400, 900),
            W("zero", 900, 1000) with { Confidence = 0f },
            W("one", 1000, 1100) with { Confidence = 1f },
            W("", 1100, 1200) with { Confidence = 0.5f },
        ];

        var blob = WordsCodec.Encode(words);
        var decoded = WordsCodec.Decode(blob);

        Assert.Equal(WordsCodec.Version2Marker, BinaryPrimitives.ReadInt32LittleEndian(blob));
        Assert.Equal(words.Select(w => w.Text), decoded.Select(w => w.Text));
        Assert.Equal(words.Select(w => w.Start), decoded.Select(w => w.Start));
        Assert.Equal(words.Select(w => w.End), decoded.Select(w => w.End));
        Assert.Equal(0.42f, decoded[0].Confidence!.Value, 1f / 254);
        Assert.Null(decoded[1].Confidence);
        Assert.Equal(0f, decoded[2].Confidence);
        Assert.Equal(1f, decoded[3].Confidence);
        Assert.Equal(0.5f, decoded[4].Confidence!.Value, 1f / 254);
    }

    [Fact]
    public void V2_Layout_IsTheMarker_ThenV1WithAConfidenceByteAfterEachWord()
    {
        var blob = WordsCodec.Encode([W("ab", 1, 2) with { Confidence = 0.5f }, W("c", 3, 4)]);

        Assert.Equal(
            new byte[]
            {
                0xFE, 0xFF, 0xFF, 0xFF, 2, 0, 0, 0,
                1, 0, 0, 0, 2, 0, 0, 0, 2, 0, (byte)'a', (byte)'b', 127,
                3, 0, 0, 0, 4, 0, 0, 0, 1, 0, (byte)'c', 255,
            },
            blob);
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 254)]
    [InlineData(0.6f, 152)]
    [InlineData(-0.5f, 0)]
    [InlineData(7f, 254)]
    [InlineData(float.NaN, 255)]
    public void ConfidenceByte_IsRoundedOn254Steps_AndClamped(float confidence, int expected) =>
        Assert.Equal((byte)expected, WordsCodec.ToByte(confidence));

    [Fact]
    public void ConfidenceByte_RoundTripsWithinHalfAStep()
    {
        for (var b = 0; b < 255; b++)
        {
            Assert.Equal((byte)b, WordsCodec.ToByte(WordsCodec.FromByte((byte)b)));
        }

        for (var c = 0f; c <= 1f; c += 0.001f)
        {
            Assert.InRange(WordsCodec.FromByte(WordsCodec.ToByte(c))!.Value, c - (0.5f / 254) - 1e-6f, c + (0.5f / 254) + 1e-6f);
        }

        // Well clear of the threshold a word stays on its side of it.
        Assert.True(WordsCodec.FromByte(WordsCodec.ToByte(0.59f)) < TranscriptWord.UncertainThreshold);
        Assert.False(WordsCodec.FromByte(WordsCodec.ToByte(0.61f)) < TranscriptWord.UncertainThreshold);
        Assert.Null(WordsCodec.FromByte(WordsCodec.UnknownConfidence));
    }

    [Fact]
    public void V2_Truncated_Throws()
    {
        var blob = WordsCodec.Encode([W("слово", 0, 10) with { Confidence = 0.3f }, W("b", 10, 20)]);

        for (var length = 1; length < blob.Length; length++)
        {
            var cut = blob.AsSpan(0, length).ToArray();
            Assert.Throws<InvalidDataException>(() => WordsCodec.Decode(cut));
        }

        Assert.Throws<InvalidDataException>(() => WordsCodec.Decode([.. blob, 0]));
    }

    [Fact]
    public void EmptyList_RoundtripsToEmpty_AndEmptyBlobDecodesToEmpty()
    {
        var blob = WordsCodec.Encode([]);

        Assert.Equal(4, blob.Length);
        Assert.Empty(WordsCodec.Decode(blob));
        Assert.Empty(WordsCodec.Decode([]));
    }

    [Fact]
    public void Layout_IsLittleEndian_CountThenStartEndLengthBytes()
    {
        var blob = WordsCodec.Encode([W("ab", 1, 2)]);

        Assert.Equal(
            new byte[] { 1, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 2, 0, (byte)'a', (byte)'b' },
            blob);
    }

    [Fact]
    public void EmptyWordText_Roundtrips()
    {
        Assert.Equal([W("", 5, 6)], WordsCodec.Decode(WordsCodec.Encode([W("", 5, 6)])));
    }

    [Fact]
    public void TooLongWord_Throws()
    {
        var word = new TranscriptWord(new string('x', ushort.MaxValue + 1), TimeSpan.Zero, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => WordsCodec.Encode([word]));
    }

    [Fact]
    public void Truncated_Throws()
    {
        var blob = WordsCodec.Encode([W("слово", 0, 10)]);

        for (var length = 1; length < blob.Length; length++)
        {
            var cut = blob.AsSpan(0, length).ToArray();
            Assert.Throws<InvalidDataException>(() => WordsCodec.Decode(cut));
        }
    }

    [Fact]
    public void TrailingBytes_AndBogusCount_Throw()
    {
        var blob = WordsCodec.Encode([W("a", 0, 1)]);
        Assert.Throws<InvalidDataException>(() => WordsCodec.Decode([.. blob, 0]));

        var bogus = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(bogus, int.MaxValue);
        Assert.Throws<InvalidDataException>(() => WordsCodec.Decode(bogus));
        BinaryPrimitives.WriteInt32LittleEndian(bogus, -1);
        Assert.Throws<InvalidDataException>(() => WordsCodec.Decode(bogus));
    }
}
