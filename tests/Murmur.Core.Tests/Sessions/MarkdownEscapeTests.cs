using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Core.Tests.Sessions;

/// <summary>The Markdown export shows user and transcript text as written: <see cref="MarkdownText"/> and its use in <see cref="TranscriptExport"/>.</summary>
public sealed class MarkdownEscapeTests
{
    private static readonly SessionId Id = SessionId.Parse("0198a3b2c4d57e6f8091a2b3c4d5e6f7");

    private static SessionRecord Session(string title) => new()
    {
        Id = Id,
        Title = title,
        StartedAt = new DateTimeOffset(2026, 10, 18, 10, 30, 0, TimeSpan.Zero),
        Duration = new TimeSpan(0, 47, 12),
        State = SessionState.Ready,
    };

    private static Transcript TranscriptOf(params string[] lines) =>
        new(Id, lines.Select((text, i) => new TranscriptSegment(i % 2 == 0 ? Speaker.Me : Speaker.Others, TimeSpan.FromSeconds(10 * i), TimeSpan.FromSeconds((10 * i) + 5), text, [])).ToList(), "parakeet", "en");

    [Theory]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("a*b*", "a\\*b\\*")]
    [InlineData("snake_case_name", "snake\\_case\\_name")]
    [InlineData("run `ls`", "run \\`ls\\`")]
    [InlineData("Q4 <roadmap>", "Q4 \\<roadmap\\>")]
    [InlineData("see [docs](x)", "see \\[docs\\](x)")]
    [InlineData("a > b", "a \\> b")]
    [InlineData("issue #5 is # not a heading", "issue #5 is # not a heading")]
    [InlineData("plain text, 3.5 and 1-2 and a - b", "plain text, 3.5 and 1-2 and a - b")]
    [InlineData("Q&A and R&D fine; ok", "Q&A and R&D fine; ok")]
    [InlineData("&amp; and &#35; and &lt;", "\\&amp; and \\&#35; and \\&lt;")]
    [InlineData("", "")]
    public void Escape_EscapesTheSpecialCharactersEverywhere(string text, string expected) =>
        Assert.Equal(expected, MarkdownText.Escape(text));

    [Theory]
    [InlineData("# heading", "\\# heading")]
    [InlineData("## heading", "\\## heading")]
    [InlineData("#hashtag", "\\#hashtag")]
    [InlineData("   # indented", "\\# indented")]
    [InlineData("- item", "\\- item")]
    [InlineData("-", "\\-")]
    [InlineData("---", "\\---")]
    [InlineData("-5 degrees", "-5 degrees")]
    [InlineData("+ item", "\\+ item")]
    [InlineData("+1 from me", "+1 from me")]
    [InlineData("> quote", "\\> quote")]
    [InlineData("1. item", "1\\. item")]
    [InlineData("42) item", "42\\) item")]
    [InlineData("12.", "12\\.")]
    [InlineData("1.5 million", "1.5 million")]
    [InlineData("2026 was a year", "2026 was a year")]
    [InlineData("===", "\\===")]
    [InlineData("~~~", "\\~~~")]
    public void Escape_EscapesWhatMakesTheLineABlock(string text, string expected) =>
        Assert.Equal(expected, MarkdownText.Escape(text));

    [Fact]
    public void Escape_OnlyEscapesTheLineStartOfTheValue() =>
        Assert.Equal("a # b - c 1. d", MarkdownText.Escape("a # b - c 1. d"));

    [Theory]
    [InlineData("one\ntwo", "one two")]
    [InlineData("one\r\ntwo", "one two")]
    [InlineData("one\r\n\r\n  two  \n", "one two")]
    [InlineData("first\n# not a heading", "first # not a heading")]
    [InlineData("first\n1. item", "first 1. item")]
    [InlineData("\n# heading\n", "\\# heading")]
    public void Escape_CollapsesLineBreaksIntoOneLine(string text, string expected) =>
        Assert.Equal(expected, MarkdownText.Escape(text));

    [Fact]
    public void Escape_ReturnsTheSameInstanceWhenThereIsNothingToEscape()
    {
        var text = "Nothing special here, 12:30 - done.";
        Assert.Same(text, MarkdownText.Escape(text));
    }

    [Fact]
    public void EscapeBlock_EscapesEveryLineAndKeepsTheParagraphs() =>
        Assert.Equal(
            "Intro \\*x\\*\n\n\\# Not a heading\n1\\. not a list\r".Replace("\r", string.Empty),
            MarkdownText.EscapeBlock("  Intro *x*\r\n\r\n# Not a heading\r\n1. not a list\n"));

    [Fact]
    public void Markdown_EscapesTheTitleAndEveryLineOfTheTranscript()
    {
        var markdown = TranscriptExport.ToMarkdown(
            Session("Q4 <roadmap> *draft* #1 `x` [a] _b_ \\ >"),
            TranscriptOf("Q4 <roadmap> *draft* _b_ `x` [a] \\ >", "# not a heading", "1. not a list", "- not a bullet", "> not a quote", "+ not a bullet", "two\nlines # here"));

        Assert.StartsWith("# Q4 \\<roadmap\\> \\*draft\\* #1 \\`x\\` \\[a\\] \\_b\\_ \\\\ \\>\n\n2026-10-18 10:30 · 47:12 · en · parakeet\n", markdown);
        Assert.Contains("**You** `00:00`  \nQ4 \\<roadmap\\> \\*draft\\* \\_b\\_ \\`x\\` \\[a\\] \\\\ \\>\n\n", markdown);
        Assert.Contains("**Others** `00:10`  \n\\# not a heading\n\n", markdown);
        Assert.Contains("**You** `00:20`  \n1\\. not a list\n\n", markdown);
        Assert.Contains("**Others** `00:30`  \n\\- not a bullet\n\n", markdown);
        Assert.Contains("**You** `00:40`  \n\\> not a quote\n\n", markdown);
        Assert.Contains("**Others** `00:50`  \n\\+ not a bullet\n\n", markdown);
        Assert.Contains("**You** `01:00`  \ntwo lines # here\n", markdown);
    }

    [Fact]
    public void Markdown_WithoutHeaderLine_StillEscapesTheLineStart()
    {
        var options = new ExportOptions(Summary: false, ActionItems: false, Timestamps: false, SpeakerNames: false);
        var markdown = TranscriptExport.Format(ExportFormat.Markdown, Session("T"), TranscriptOf("# loud", "- dash"), null, null, options, null, TestContext.Current.CancellationToken);

        Assert.EndsWith("## Transcript\n\n\\# loud\n\n\\- dash\n", markdown);
    }

    [Fact]
    public void Markdown_EscapesTheSpeakerNames()
    {
        var labels = new ExportLabels("*Me*", "Bob_1 <b>");

        var markdown = TranscriptExport.ToMarkdown(Session("T"), TranscriptOf("a", "b"), null, labels);

        Assert.Contains("**\\*Me\\*** `00:00`", markdown);
        Assert.Contains("**Bob\\_1 \\<b\\>** `00:10`", markdown);
    }

    [Fact]
    public void Markdown_EscapesTheSummaryTextAndKeepsItsOwnMarkup()
    {
        var summary = new SessionSummary(
            Id,
            "Intro with <tags> and *stars*.\n\n# Fake heading\n1. fake list",
            [new SummaryItem("# key *point*", TimeSpan.FromSeconds(75)), new SummaryItem("- nested", null)],
            [new SummaryItem("Send [the] invite_", TimeSpan.FromSeconds(95))],
            "gemma",
            new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

        var markdown = TranscriptExport.ToMarkdown(Session("T"), TranscriptOf("a"), summary);

        Assert.Contains("### Overview\n\nIntro with \\<tags\\> and \\*stars\\*.\n\n\\# Fake heading\n1\\. fake list\n\n", markdown);
        Assert.Contains("### Key points\n\n- \\# key \\*point\\* (`01:15`)\n- \\- nested\n\n", markdown);
        Assert.Contains("### Action items\n\n- [ ] Send \\[the\\] invite\\_ (`01:35`)\n\n", markdown);
    }

    [Fact]
    public void Markdown_KeepsTheStructureOfTheFileItWritesItself()
    {
        var markdown = TranscriptExport.ToMarkdown(Session("Title"), TranscriptOf("Hello."));

        Assert.Equal("# Title\n\n2026-10-18 10:30 · 47:12 · en · parakeet\n\n## Transcript\n\n**You** `00:00`  \nHello.\n", markdown);
    }

    [Fact]
    public void TheFormatEntryPoint_EscapesMarkdownAndNotTheDocxPreview()
    {
        var session = Session("A *b* <c>");
        var transcript = TranscriptOf("# x *y*");

        var markdown = TranscriptExport.Format(ExportFormat.Markdown, session, transcript);
        var preview = TranscriptExport.Format(ExportFormat.Docx, session, transcript);

        Assert.Contains("# A \\*b\\* \\<c\\>", markdown);
        Assert.Contains("\\# x \\*y\\*", markdown);
        Assert.Contains("# A *b* <c>", preview);
        Assert.Contains("\n# x *y*\n", preview);
    }

    [Fact]
    public void TheOtherFormats_AreNotEscaped()
    {
        var session = Session("A *b* _c_ [d]");
        var transcript = TranscriptExport.Format(ExportFormat.PlainText, session, TranscriptOf("# x *y* `z` [w] \\"));

        Assert.Equal("A *b* _c_ [d]\n2026-10-18 10:30 · 47:12 · en · parakeet\n\n[00:00] You: # x *y* `z` [w] \\\n", transcript);
        Assert.Contains("You: # x *y* `z` [w] \\\n", TranscriptExport.ToSrt(session, TranscriptOf("# x *y* `z` [w] \\")));
        Assert.Contains("# x *y* `z` [w] \\\n", TranscriptExport.ToWebVtt(session, TranscriptOf("# x *y* `z` [w] \\")));
        Assert.StartsWith("WEBVTT - A *b* _c_ [d]\n", TranscriptExport.ToWebVtt(session, TranscriptOf("x")));
    }
}
