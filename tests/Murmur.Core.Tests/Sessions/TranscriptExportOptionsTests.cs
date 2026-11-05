using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Core.Tests.Sessions;

/// <summary>The export options (what to include) and the range (which part of the timeline), for every format.</summary>
public sealed class TranscriptExportOptionsTests
{
    private static readonly SessionId Id = SessionId.Parse("0198a3b2c4d57e6f8091a2b3c4d5e6f7");

    private static readonly SessionRecord Session = new()
    {
        Id = Id,
        Title = "Client demo",
        StartedAt = new DateTimeOffset(2026, 10, 18, 10, 30, 0, TimeSpan.Zero),
        Duration = new TimeSpan(0, 47, 12),
        State = SessionState.Ready,
    };

    private static TranscriptSegment Segment(Speaker speaker, double start, double end, string text) =>
        new(speaker, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, []);

    private static readonly Transcript Transcript = new(
        Id,
        [
            Segment(Speaker.Me, 0, 10, "First line."),
            Segment(Speaker.Others, 10, 20, "Second line."),
            Segment(Speaker.Me, 20, 30, "Third line."),
            Segment(Speaker.Others, 40, 40, "Point line."),
        ],
        "parakeet",
        "en");

    private static readonly SessionSummary Summary = new(
        Id,
        "The overview text.",
        [new SummaryItem("A key point", TimeSpan.FromSeconds(75))],
        [new SummaryItem("Send the invite", TimeSpan.FromSeconds(95))],
        "gemma",
        new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    private static string Format(ExportFormat format, ExportOptions? options = null, ExportRange? range = null, SessionSummary? summary = null) =>
        TranscriptExport.Format(format, Session, Transcript, summary ?? Summary, null, options ?? ExportOptions.Default, range);

    [Fact]
    public void TheDefaultOptions_IncludeEverything()
    {
        Assert.Equal(new ExportOptions(true, true, true, true), ExportOptions.Default);
    }

    [Theory]
    [InlineData(ExportFormat.Markdown)]
    [InlineData(ExportFormat.PlainText)]
    [InlineData(ExportFormat.Srt)]
    [InlineData(ExportFormat.WebVtt)]
    [InlineData(ExportFormat.Docx)]
    public void TheOldOverload_IsTheNewOneWithTheDefaultOptions(ExportFormat format)
    {
        var summary = format is ExportFormat.Markdown or ExportFormat.Docx ? Summary : null;

        Assert.Equal(
            TranscriptExport.Format(format, Session, Transcript, summary, null, ExportOptions.Default, null, TestContext.Current.CancellationToken),
            TranscriptExport.Format(format, Session, Transcript, Summary));
    }

    [Fact]
    public void TheOldOverload_KeepsThePlainTextWithoutTheSummary()
    {
        Assert.DoesNotContain("SUMMARY", TranscriptExport.Format(ExportFormat.PlainText, Session, Transcript, Summary), StringComparison.Ordinal);
    }

    // Summary ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ExportFormat.Markdown, "## Summary", "The overview text.", "A key point")]
    [InlineData(ExportFormat.PlainText, "SUMMARY", "The overview text.", "A key point")]
    public void Summary_OffDropsTheOverviewAndTheKeyPoints_OnKeepsThem(ExportFormat format, string heading, string overview, string keyPoint)
    {
        var on = Format(format, new ExportOptions(Summary: true));
        var off = Format(format, new ExportOptions(Summary: false));

        Assert.Contains(heading, on, StringComparison.Ordinal);
        Assert.Contains(overview, on, StringComparison.Ordinal);
        Assert.Contains(keyPoint, on, StringComparison.Ordinal);
        Assert.DoesNotContain(heading, off, StringComparison.Ordinal);
        Assert.DoesNotContain(overview, off, StringComparison.Ordinal);
        Assert.DoesNotContain(keyPoint, off, StringComparison.Ordinal);
        Assert.Contains("Send the invite", off, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExportFormat.Markdown, "Action items")]
    [InlineData(ExportFormat.PlainText, "ACTION ITEMS")]
    public void ActionItems_OffDropsThem_AndNothingElse(ExportFormat format, string heading)
    {
        var on = Format(format, new ExportOptions(ActionItems: true));
        var off = Format(format, new ExportOptions(ActionItems: false));

        Assert.Contains(heading, on, StringComparison.Ordinal);
        Assert.Contains("Send the invite", on, StringComparison.Ordinal);
        Assert.DoesNotContain(heading, off, StringComparison.Ordinal);
        Assert.DoesNotContain("Send the invite", off, StringComparison.Ordinal);
        Assert.Contains("The overview text.", off, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutASummary_NoSummarySectionIsWritten_WhateverTheOptions()
    {
        var markdown = TranscriptExport.Format(ExportFormat.Markdown, Session, Transcript, null, null, ExportOptions.Default, null, TestContext.Current.CancellationToken);
        var text = TranscriptExport.Format(ExportFormat.PlainText, Session, Transcript, null, null, ExportOptions.Default, null, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("## Summary", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("SUMMARY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TRANSCRIPT", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExportFormat.Srt)]
    [InlineData(ExportFormat.WebVtt)]
    public void Subtitles_IgnoreTheSummaryAndTheActionItems(ExportFormat format)
    {
        var all = Format(format, ExportOptions.Default);
        var none = Format(format, new ExportOptions(Summary: false, ActionItems: false));

        Assert.Equal(all, none);
        Assert.DoesNotContain("overview", all, StringComparison.Ordinal);
        Assert.DoesNotContain("invite", all, StringComparison.Ordinal);
    }

    // Timestamps ------------------------------------------------------------------------------------------------

    [Fact]
    public void Markdown_Timestamps_AreTheTimeNextToTheSpeaker_AndOnTheItems()
    {
        var on = Format(ExportFormat.Markdown, new ExportOptions(Timestamps: true));
        var off = Format(ExportFormat.Markdown, new ExportOptions(Timestamps: false));

        Assert.Contains("**You** `00:00`  \nFirst line.", on, StringComparison.Ordinal);
        Assert.Contains("- [ ] Send the invite (`01:35`)", on, StringComparison.Ordinal);
        Assert.Contains("**You**  \nFirst line.", off, StringComparison.Ordinal);
        Assert.Contains("- [ ] Send the invite\n", off, StringComparison.Ordinal);
        Assert.Contains("- A key point\n", off, StringComparison.Ordinal);
        Assert.DoesNotContain('`', off);
    }

    [Fact]
    public void PlainText_Timestamps_AreTheBracketedTime()
    {
        var on = Format(ExportFormat.PlainText, new ExportOptions(Timestamps: true));
        var off = Format(ExportFormat.PlainText, new ExportOptions(Timestamps: false));

        Assert.Contains("[00:10] Others: Second line.\n", on, StringComparison.Ordinal);
        Assert.Contains("• Send the invite (01:35)\n", on, StringComparison.Ordinal);
        Assert.Contains("Others: Second line.\n", off, StringComparison.Ordinal);
        Assert.DoesNotContain("[00:10]", off, StringComparison.Ordinal);
        Assert.Contains("• Send the invite\n", off, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExportFormat.Srt, "00:00:10,000 --> 00:00:20,000")]
    [InlineData(ExportFormat.WebVtt, "00:00:10.000 --> 00:00:20.000")]
    public void Subtitles_AlwaysCarryTheirTimes(ExportFormat format, string cue)
    {
        Assert.Contains(cue, Format(format, new ExportOptions(Timestamps: false)), StringComparison.Ordinal);
        Assert.Contains(cue, Format(format, new ExportOptions(Timestamps: true)), StringComparison.Ordinal);
    }

    // Speaker names ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ExportFormat.Markdown, "**You**", "**Others**")]
    [InlineData(ExportFormat.PlainText, "You: ", "Others: ")]
    [InlineData(ExportFormat.Srt, "You: ", "Others: ")]
    [InlineData(ExportFormat.WebVtt, "<v You>", "<v Others>")]
    public void SpeakerNames_OffLeavesNoLabels(ExportFormat format, string me, string others)
    {
        var on = Format(format, new ExportOptions(SpeakerNames: true));
        var off = Format(format, new ExportOptions(SpeakerNames: false));

        Assert.Contains(me, on, StringComparison.Ordinal);
        Assert.Contains(others, on, StringComparison.Ordinal);
        Assert.DoesNotContain(me, off, StringComparison.Ordinal);
        Assert.DoesNotContain(others, off, StringComparison.Ordinal);
        Assert.Contains("Second line.", off, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNothingAroundIt_ASegmentIsJustItsText()
    {
        var off = new ExportOptions(Summary: false, ActionItems: false, Timestamps: false, SpeakerNames: false);

        Assert.Contains("## Transcript\n\nFirst line.\n\nSecond line.\n\nThird line.", Format(ExportFormat.Markdown, off), StringComparison.Ordinal);
        Assert.EndsWith("\nFirst line.\nSecond line.\nThird line.\nPoint line.\n", Format(ExportFormat.PlainText, off), StringComparison.Ordinal);
    }

    // Range -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Range_KeepsTheSegmentsThatOverlapIt_AndSrtKeepsTheSessionTimes()
    {
        var range = new ExportRange(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(25));

        var srt = Format(ExportFormat.Srt, range: range);

        Assert.DoesNotContain("First line.", srt, StringComparison.Ordinal);
        Assert.StartsWith("1\n00:00:10,000 --> 00:00:20,000\nOthers: Second line.\n\n2\n00:00:20,000 --> 00:00:30,000\nYou: Third line.\n\n", srt, StringComparison.Ordinal);
        Assert.DoesNotContain("Point line.", srt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10, 20, "Second line.", "First line.")]
    [InlineData(0, 10, "First line.", "Second line.")]
    public void Range_IsHalfOpen_ASegmentThatEndsWhereItStartsIsOut(double start, double end, string included, string excluded)
    {
        var text = Format(ExportFormat.PlainText, range: new ExportRange(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)));

        Assert.Contains(included, text, StringComparison.Ordinal);
        Assert.DoesNotContain(excluded, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Range_ASegmentWithoutLength_IsAPoint()
    {
        Assert.Contains("Point line.", Format(ExportFormat.PlainText, range: new ExportRange(TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(45))), StringComparison.Ordinal);
        Assert.DoesNotContain("Point line.", Format(ExportFormat.PlainText, range: new ExportRange(TimeSpan.FromSeconds(41), TimeSpan.FromSeconds(50))), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExportFormat.Markdown)]
    [InlineData(ExportFormat.PlainText)]
    [InlineData(ExportFormat.WebVtt)]
    public void Range_AppliesToEveryTextFormat(ExportFormat format)
    {
        var text = Format(format, range: new ExportRange(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)));

        Assert.Contains("Second line.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("First line.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Third line.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyRange_LeavesAnEmptyTranscript_NotAnError()
    {
        var text = Format(ExportFormat.Srt, range: new ExportRange(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(6)));

        Assert.Equal(string.Empty, text);
    }

    // Heading levels --------------------------------------------------------------------------------------------

    [Fact]
    public void ActionItemsWithoutTheSummary_AreASectionOfTheirOwn_NoHeadingLevelIsSkipped()
    {
        var alone = Format(ExportFormat.Markdown, new ExportOptions(Summary: false));
        var both = Format(ExportFormat.Markdown);

        Assert.Contains("\n## Action items\n\n- [ ] Send the invite", alone, StringComparison.Ordinal);
        Assert.DoesNotContain("### Action items", alone, StringComparison.Ordinal);
        Assert.DoesNotContain("## Summary", alone, StringComparison.Ordinal);
        Assert.Contains("\n### Action items\n\n", both, StringComparison.Ordinal);
    }

    [Fact]
    public void ASummaryWithoutText_HasNoSummaryHeading_InAnyFormat()
    {
        var empty = Summary with { Overview = "  ", KeyPoints = [] };

        var markdown = Format(ExportFormat.Markdown, summary: empty);
        var plain = Format(ExportFormat.PlainText, summary: empty);

        Assert.DoesNotContain("## Summary", markdown, StringComparison.Ordinal);
        Assert.Contains("\n## Action items\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("SUMMARY", plain, StringComparison.Ordinal);
        Assert.Contains("ACTION ITEMS\n• Send the invite", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void ASummaryWithNothingAtAll_AddsNoSection()
    {
        var nothing = Summary with { Overview = string.Empty, KeyPoints = [], ActionItems = [] };

        var plain = Format(ExportFormat.PlainText, summary: nothing);

        Assert.DoesNotContain("SUMMARY", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("TRANSCRIPT", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("## Summary", Format(ExportFormat.Markdown, summary: nothing), StringComparison.Ordinal);
    }

    // The range edge cases and giving up ---------------------------------------------------------------------------

    [Theory]
    [InlineData(30, 5)]
    [InlineData(15, 15)]
    [InlineData(-5, -10)]
    public void ARangeThatEndsAtOrBeforeItsStart_HasNoSegments_WhateverTheyCover(double start, double end)
    {
        var spanning = new Transcript(Id, [Segment(Speaker.Me, 0, 60, "Spans everything."), Segment(Speaker.Others, 15, 15, "A point.")], "parakeet", "en");
        var range = new ExportRange(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));

        foreach (var format in new[] { ExportFormat.Markdown, ExportFormat.PlainText, ExportFormat.Srt, ExportFormat.WebVtt })
        {
            var text = TranscriptExport.Format(format, Session, spanning, null, null, ExportOptions.Default, range, TestContext.Current.CancellationToken);

            Assert.DoesNotContain("Spans everything.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("A point.", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(ExportFormat.Markdown)]
    [InlineData(ExportFormat.PlainText)]
    [InlineData(ExportFormat.Srt)]
    [InlineData(ExportFormat.WebVtt)]
    public void ACancelledToken_StopsTheBuild_AndALiveOneChangesNothing(ExportFormat format)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => TranscriptExport.Format(format, Session, Transcript, Summary, null, ExportOptions.Default, null, cancelled.Token));
        Assert.Equal(
            Format(format),
            TranscriptExport.Format(format, Session, Transcript, Summary, null, ExportOptions.Default, null, CancellationToken.None));
    }

    [Fact]
    public void Docx_ReturnsTheMarkdownTextAsItsPreview()
    {
        Assert.Equal(Format(ExportFormat.Markdown), Format(ExportFormat.Docx));
        Assert.Equal(".docx", TranscriptExport.Extension(ExportFormat.Docx));
    }
}
