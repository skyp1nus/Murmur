using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Core.Tests.Sessions;

/// <summary>The character budget of <see cref="TranscriptExport.Format(ExportFormat, SessionRecord, Transcript, SessionSummary?, ExportLabels?, ExportOptions, ExportRange?, int?, CancellationToken)"/>: the preview of a long transcript.</summary>
public sealed class TranscriptExportBudgetTests
{
    private const int Budget = 5000;
    private static readonly SessionId Id = SessionId.Parse("0198a3b2c4d57e6f8091a2b3c4d5e6f7");

    private static readonly SessionRecord Session = new()
    {
        Id = Id,
        Title = "Weekly planning",
        StartedAt = new DateTimeOffset(2026, 10, 18, 10, 30, 0, TimeSpan.Zero),
        Duration = TimeSpan.FromHours(3),
        State = SessionState.Ready,
    };

    private static readonly SessionSummary Summary = new(
        Id,
        "Walked through the plan.",
        [new SummaryItem("Export dialog first", TimeSpan.FromMinutes(5))],
        [new SummaryItem("Send the invite", TimeSpan.FromMinutes(95))],
        "gemma",
        DateTimeOffset.UnixEpoch);

    private static Transcript Long(int count)
    {
        var segments = new TranscriptSegment[count];
        for (var i = 0; i < count; i++)
        {
            var start = TimeSpan.FromSeconds(i * 2.7);
            segments[i] = new TranscriptSegment(
                i % 2 == 0 ? Speaker.Me : Speaker.Others,
                start,
                start + TimeSpan.FromSeconds(2.5),
                $"Line {i}: so the plan for the next sprint is to finish the export dialog & the search.",
                []);
        }

        return new Transcript(Id, segments, "parakeet", "en");
    }

    public static TheoryData<ExportFormat> TextFormats => new()
    {
        ExportFormat.Markdown,
        ExportFormat.PlainText,
        ExportFormat.Srt,
        ExportFormat.WebVtt,
    };

    private static string Full(ExportFormat format, Transcript transcript, ExportOptions options, ExportRange? range = null) =>
        TranscriptExport.Format(format, Session, transcript, Summary, null, options, range);

    private static string Budgeted(ExportFormat format, Transcript transcript, ExportOptions options, ExportRange? range = null, int budget = Budget) =>
        TranscriptExport.Format(format, Session, transcript, Summary, null, options, range, budget, TestContext.Current.CancellationToken);

    [Theory]
    [MemberData(nameof(TextFormats))]
    public void ALongTranscript_IsAPrefixOfTheFullText_UpToTheCutPoint(ExportFormat format)
    {
        var transcript = Long(4000);
        var full = Full(format, transcript, ExportOptions.Default);
        Assert.True(full.Length > 20 * Budget);

        var preview = Budgeted(format, transcript, ExportOptions.Default);

        // What the view model did before the budget existed: cut the whole text.
        Assert.Equal(TranscriptExport.CutPreview(full, Budget), preview);
        Assert.EndsWith(TranscriptExport.PreviewEllipsis, preview, StringComparison.Ordinal);
        var body = preview[..^TranscriptExport.PreviewEllipsis.Length];
        Assert.True(body.Length <= Budget);
        Assert.StartsWith(body, full, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TextFormats))]
    public void ATextThatFitsTheBudget_IsTheFullTextUnchanged(ExportFormat format)
    {
        var transcript = Long(5);
        var full = Full(format, transcript, ExportOptions.Default);

        Assert.Equal(full, Budgeted(format, transcript, ExportOptions.Default));
    }

    [Theory]
    [MemberData(nameof(TextFormats))]
    public void ABudget_AlsoHoldsWithOptionsAndARange(ExportFormat format)
    {
        var transcript = Long(4000);
        var options = new ExportOptions(Summary: false, ActionItems: false, Timestamps: false, SpeakerNames: false);
        var range = new ExportRange(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(120));

        Assert.Equal(
            TranscriptExport.CutPreview(Full(format, transcript, options, range), Budget),
            Budgeted(format, transcript, options, range));
    }

    [Fact]
    public void TheDocxPreview_AlsoStopsAtTheBudget()
    {
        var transcript = Long(4000);

        Assert.Equal(
            TranscriptExport.CutPreview(Full(ExportFormat.Docx, transcript, ExportOptions.Default), Budget),
            Budgeted(ExportFormat.Docx, transcript, ExportOptions.Default));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(40_000)]
    public void EveryBudget_GivesTheCutOfTheFullText(int budget)
    {
        var transcript = Long(2000);

        Assert.Equal(
            TranscriptExport.CutPreview(Full(ExportFormat.Markdown, transcript, ExportOptions.Default), budget),
            Budgeted(ExportFormat.Markdown, transcript, ExportOptions.Default, budget: budget));
    }

    [Fact]
    public void TheBuild_StopsAtTheBudget_InsteadOfFormattingTheRest()
    {
        var transcript = Long(4000);
        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = Full(ExportFormat.Srt, transcript, ExportOptions.Default);
        var whole = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        _ = Budgeted(ExportFormat.Srt, transcript, ExportOptions.Default);
        var cut = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(cut * 20 < whole, $"budgeted {cut} B, whole {whole} B");
    }

    [Fact]
    public void ANullBudget_IsTheFullText()
    {
        var transcript = Long(300);

        Assert.Equal(
            Full(ExportFormat.PlainText, transcript, ExportOptions.Default),
            TranscriptExport.Format(ExportFormat.PlainText, Session, transcript, Summary, null, ExportOptions.Default, null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ABudgetBelowOne_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TranscriptExport.Format(ExportFormat.Markdown, Session, Long(1), Summary, null, ExportOptions.Default, null, 0, TestContext.Current.CancellationToken));

    [Fact]
    public void ACancelledBuild_StillThrows()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            TranscriptExport.Format(ExportFormat.Markdown, Session, Long(10), Summary, null, ExportOptions.Default, null, Budget, source.Token));
    }
}
