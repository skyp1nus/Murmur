using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.Core.Tests.Sessions;

public sealed class SessionNotesTests
{
    [Fact]
    public void Format_WithoutDetail_IsTheCode() =>
        Assert.Equal("rec.mic.denied", SessionNotes.Format(SessionNotes.MicrophoneDenied));

    [Fact]
    public void Format_WithDetail_AppendsItAfterABar_AndFlattensLineBreaks() =>
        Assert.Equal("rec.mic.unavailable|device busy again", SessionNotes.Format(SessionNotes.MicrophoneUnavailable, " device busy\r\nagain "));

    [Fact]
    public void FreeText_IsOneLine_SoParseKeepsItWhole()
    {
        var stored = SessionNotes.FreeText("Writing audio failed:\r\ndisk full\n at X ");

        Assert.Equal("Writing audio failed: disk full at X", stored);
        var note = Assert.Single(SessionNotes.Parse(stored));
        Assert.False(note.IsCode);
        Assert.Equal(stored, note.Text);
    }

    [Fact]
    public void Parse_SplitsCodesAndDetails()
    {
        var notes = SessionNotes.Parse("rec.mic.unavailable|a|b\nrec.only.system");

        Assert.Collection(
            notes,
            n =>
            {
                Assert.True(n.IsCode);
                Assert.Equal("rec.mic.unavailable", n.Code);
                Assert.Equal("a|b", n.Detail);
            },
            n =>
            {
                Assert.Equal("rec.only.system", n.Code);
                Assert.Null(n.Detail);
            });
    }

    [Theory]
    [InlineData("Мікрофон недоступний: Windows забороняє доступ. Записую лише системний звук.")]
    [InlineData("Writing audio failed: disk full")]
    [InlineData("rec.mic denied")]
    [InlineData("recording stopped")]
    [InlineData("rec.")]
    [InlineData("rec.Mic.denied")]
    public void Parse_TreatsAnythingElseAsFreeText(string text)
    {
        var note = Assert.Single(SessionNotes.Parse(text));

        Assert.False(note.IsCode);
        Assert.Null(note.Code);
        Assert.Equal(text, note.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void Parse_OfNothing_IsEmpty(string? text) => Assert.Empty(SessionNotes.Parse(text));

    [Fact]
    public void Join_PutsEachOnItsOwnLine_AndSkipsMissingParts()
    {
        Assert.Equal("a\nb", SessionNotes.Join("a", "b"));
        Assert.Equal("a", SessionNotes.Join("a", null));
        Assert.Equal("b", SessionNotes.Join(null, "b"));
        Assert.Null(SessionNotes.Join(null, null));
    }

    [Fact]
    public void Has_FindsACodeWithOrWithoutDetail()
    {
        Assert.True(SessionNotes.Has("proc.index.failed|boom", SessionNotes.IndexFailed));
        Assert.True(SessionNotes.Has("Screen failed\nproc.index.failed", SessionNotes.IndexFailed));
        Assert.False(SessionNotes.Has("proc.index.failedx", SessionNotes.IndexFailed));
        Assert.False(SessionNotes.Has(null, SessionNotes.IndexFailed));
    }

    [Fact]
    public void EveryKnownCode_IsUniqueAndWellFormed()
    {
        Assert.Equal(SessionNotes.KnownCodes.Count, SessionNotes.KnownCodes.Distinct(StringComparer.Ordinal).Count());
        foreach (var code in SessionNotes.KnownCodes)
        {
            var note = Assert.Single(SessionNotes.Parse(code));
            Assert.Equal(code, note.Code);
        }
    }

    [Fact]
    public void EveryRecorderWarningCode_HasAKnownStoredCode()
    {
        foreach (var warning in Enum.GetValues<RecorderWarningCode>())
        {
            Assert.Contains(SessionNotes.CodeOf(warning), SessionNotes.KnownCodes);
        }

        // Each warning has a code of its own.
        var codes = Enum.GetValues<RecorderWarningCode>().Select(SessionNotes.CodeOf).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }
}
