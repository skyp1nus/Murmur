using BenchmarkDotNet.Attributes;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Benchmarks;

/// <summary>
/// The export of a three-hour meeting (about 4000 segments): the text formats (the dialog's live preview formats them after every change
/// of an option, off the UI thread) and the hand-written .docx. Not an audio callback, but a path the user waits for: the allocation
/// figures show what a long export costs.
/// </summary>
[MemoryDiagnoser]
public class TranscriptExportBenchmarks
{
    private SessionRecord _session = null!;
    private Transcript _transcript = null!;
    private SessionSummary _summary = null!;

    [GlobalSetup]
    public void Setup()
    {
        var id = SessionId.New();
        _session = new SessionRecord
        {
            Id = id,
            Title = "Weekly planning",
            StartedAt = new DateTimeOffset(2026, 10, 18, 10, 30, 0, TimeSpan.Zero),
            Duration = TimeSpan.FromHours(3),
            State = SessionState.Ready,
        };
        var segments = new TranscriptSegment[4000];
        for (var i = 0; i < segments.Length; i++)
        {
            var start = TimeSpan.FromSeconds(i * 2.7);
            segments[i] = new TranscriptSegment(
                i % 2 == 0 ? Speaker.Me : Speaker.Others,
                start,
                start + TimeSpan.FromSeconds(2.5),
                "So the plan for the next sprint is to finish the export dialog, the search and the summaries, & then demo it.",
                []);
        }

        _transcript = new Transcript(id, segments, "parakeet", "en");
        _summary = new SessionSummary(
            id,
            "Walked through the plan for the next sprint.",
            [new SummaryItem("Export dialog first", TimeSpan.FromMinutes(5))],
            [new SummaryItem("Send the invite", TimeSpan.FromMinutes(95))],
            "gemma",
            DateTimeOffset.UnixEpoch);
    }

    [Benchmark]
    public string Markdown() =>
        TranscriptExport.Format(ExportFormat.Markdown, _session, _transcript, _summary, null, ExportOptions.Default, null);

    /// <summary>The escape of one transcript line with nothing to escape (the same instance back) and one with markup in it.</summary>
    [Benchmark]
    public string EscapePlainLine() => MarkdownText.Escape(_transcript.Segments[0].Text);

    [Benchmark]
    public string EscapeMarkupLine() => MarkdownText.Escape("# Q4 <roadmap> *draft* and `code`, [link] and snake_case\nsecond line");

    [Benchmark]
    public string PlainText() =>
        TranscriptExport.Format(ExportFormat.PlainText, _session, _transcript, _summary, null, ExportOptions.Default, null);

    [Benchmark]
    public string SrtOfAChapter() =>
        TranscriptExport.Format(ExportFormat.Srt, _session, _transcript, null, null, ExportOptions.Default, new ExportRange(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60)));

    [Benchmark]
    public byte[] Docx() =>
        TranscriptExport.ToDocx(_session, _transcript, _summary, null, ExportOptions.Default, null);
}
