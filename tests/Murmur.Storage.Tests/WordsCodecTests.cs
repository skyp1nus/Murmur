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
