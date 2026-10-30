using Murmur.App.ViewModels;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

public sealed class AnswerTextTests
{
    private static readonly SessionId Session = SessionId.New();

    private static CitationChipViewModel Chip(double seconds) =>
        new(new Citation(Session, TimeSpan.FromSeconds(seconds), "x", "q"), _ => { });

    private static IReadOnlyList<AnswerParagraph> Parse(string text, params double[] cited)
    {
        var chips = cited.Select(Chip).ToArray();
        return AnswerText.Parse(text, at => chips.FirstOrDefault(c => c.Citation.At == at));
    }

    private static string Describe(AnswerParagraph paragraph) =>
        string.Concat(paragraph.Parts.Select(p => p.Kind switch
        {
            AnswerPartKind.Text => p.Text,
            AnswerPartKind.Chip => "{" + p.Chip!.Citation.At.TotalSeconds + "}",
            _ => "/",
        }));

    [Fact]
    public void AMarkerOfACitedMoment_BecomesAChipInTheLine()
    {
        var paragraphs = Parse("Moved to next sprint [12:52]. The client said so [12:40].", 772, 760);

        var paragraph = Assert.Single(paragraphs);
        Assert.Equal("Moved to next sprint {772}. The client said so {760}.", Describe(paragraph));
    }

    [Fact]
    public void ABlankLineStartsAParagraph_AndASingleNewlineIsALineBreak()
    {
        var paragraphs = Parse("First [00:05]\nsecond line\n\nNext paragraph", 5);

        Assert.Equal(["First {5}/second line", "Next paragraph"], paragraphs.Select(Describe));
    }

    [Fact]
    public void AMarkerThatMatchesNoCitation_StaysAsWritten()
    {
        var paragraphs = Parse("Maybe [09:09] or [see below] or [1:02:03].", 3723);

        Assert.Equal("Maybe [09:09] or [see below] or {3723}.", Describe(Assert.Single(paragraphs)));
    }

    [Theory]
    [InlineData("0:05", 5)]
    [InlineData("12:34", 754)]
    [InlineData("1:02:03", 3723)]
    [InlineData("12:00–12:45", 720)]
    [InlineData("12:00-12:45", 720)]
    public void TheTimestampSyntaxOfTheResolver_IsRecognised(string marker, double seconds)
    {
        Assert.True(AnswerText.TryParseMarker(marker, out var at));
        Assert.Equal(TimeSpan.FromSeconds(seconds), at);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("12:5")]
    [InlineData("12:75")]
    [InlineData("a:bc")]
    [InlineData("1:70:00")]
    [InlineData("12:00–x")]
    [InlineData("")]
    public void ThingsThatAreNotTimestamps_AreNotMarkers(string marker) =>
        Assert.False(AnswerText.TryParseMarker(marker, out _));

    [Fact]
    public void EmptyText_HasNoParagraphs() => Assert.Empty(Parse("  \n \n"));
}
