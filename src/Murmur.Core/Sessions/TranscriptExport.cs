using System.Globalization;
using System.Text;
using Murmur.Core.Intelligence;
using Murmur.Core.Transcription;

namespace Murmur.Core.Sessions;

/// <summary>
/// The words of an export that are not the recording's own: the two speakers and the headings of the Markdown file. Core has no UI
/// language, so the caller passes them (the app uses its localized resources); <see cref="English"/> is the neutral default.
/// </summary>
public sealed record ExportLabels(
    string Me,
    string Others,
    string Summary = "Summary",
    string Overview = "Overview",
    string KeyPoints = "Key points",
    string ActionItems = "Action items",
    string Transcript = "Transcript",
    string Untitled = "Untitled recording")
{
    public static ExportLabels English { get; } = new("You", "Others");
}

/// <summary>What an export includes. Srt/WebVtt ignore Summary/ActionItems and always include timestamps.</summary>
public sealed record ExportOptions(bool Summary = true, bool ActionItems = true, bool Timestamps = true, bool SpeakerNames = true)
{
    public static readonly ExportOptions Default = new();
}

/// <summary>A part of the timeline to export; null = the whole recording.</summary>
public readonly record struct ExportRange(TimeSpan Start, TimeSpan End);

/// <summary>
/// Pure formatters that turn a recording into the text of an export file: Markdown (with the summary when there is one), plain text,
/// SRT subtitles and WebVTT, and the .docx (<see cref="ToDocx"/>). Lines end with <c>\n</c> in every text format, times use the invariant
/// culture, so the output of the same recording is the same on every machine. The caller writes the string as UTF-8.
/// </summary>
public static class TranscriptExport
{
    /// <summary>The file extension (with the dot) of each format, for the save dialog.</summary>
    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Markdown => ".md",
        ExportFormat.PlainText => ".txt",
        ExportFormat.Srt => ".srt",
        ExportFormat.WebVtt => ".vtt",
        ExportFormat.Docx => ".docx",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>
    /// Formats the recording as <paramref name="format"/> with the default options and the whole timeline
    /// (<paramref name="summary"/> only matters for Markdown, as before the options existed).
    /// </summary>
    public static string Format(ExportFormat format, SessionRecord session, Transcript transcript, SessionSummary? summary = null, ExportLabels? labels = null) =>
        Format(format, session, transcript, format is ExportFormat.Markdown or ExportFormat.Docx ? summary : null, labels, ExportOptions.Default, null);

    /// <summary>
    /// Formats the recording as <paramref name="format"/>: only what <paramref name="options"/> asks for, and only the segments that
    /// overlap <paramref name="range"/> (the whole recording when <c>null</c>). <see cref="ExportFormat.Docx"/> returns the Markdown text
    /// the .docx carries (its preview); the file itself is <see cref="ToDocx"/>.
    /// </summary>
    public static string Format(
        ExportFormat format,
        SessionRecord session,
        Transcript transcript,
        SessionSummary? summary,
        ExportLabels? labels,
        ExportOptions options,
        ExportRange? range) =>
        Format(format, session, transcript, summary, labels, options, range, CancellationToken.None);

    /// <summary>
    /// The same, for a caller that may give up (the live preview of a long transcript): the token is checked between segments and a
    /// cancelled build throws <see cref="OperationCanceledException"/> instead of running to the end.
    /// </summary>
    public static string Format(
        ExportFormat format,
        SessionRecord session,
        Transcript transcript,
        SessionSummary? summary,
        ExportLabels? labels,
        ExportOptions options,
        ExportRange? range,
        CancellationToken cancellationToken) =>
        Format(format, session, transcript, summary, labels, options, range, null, cancellationToken);

    /// <summary>
    /// The same with a character budget for a preview: once the text built so far is longer than <paramref name="maxChars"/> the build
    /// stops (the next segment is never formatted) and the result is cut as a preview is: at the last line break within the budget,
    /// followed by <see cref="PreviewEllipsis"/>. The result is the full export up to that cut point plus the ellipsis; when the full
    /// export fits the budget it is the full export, unchanged. <c>null</c> = no budget.
    /// </summary>
    public static string Format(
        ExportFormat format,
        SessionRecord session,
        Transcript transcript,
        SessionSummary? summary,
        ExportLabels? labels,
        ExportOptions options,
        ExportRange? range,
        int? maxChars,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars ?? 1, 1, nameof(maxChars));
        labels ??= ExportLabels.English;
        var text = format switch
        {
            ExportFormat.Markdown => Markdown(session, transcript, summary, labels, options, range, cancellationToken, escape: true, maxChars),
            // The preview of the .docx: the text as the file carries it, which is not Markdown, so nothing is escaped.
            ExportFormat.Docx => Markdown(session, transcript, summary, labels, options, range, cancellationToken, escape: false, maxChars),
            ExportFormat.PlainText => PlainText(session, transcript, summary, labels, options, range, cancellationToken, maxChars),
            ExportFormat.Srt => Srt(transcript, labels, options, range, cancellationToken, maxChars),
            ExportFormat.WebVtt => WebVtt(session, transcript, labels, options, range, cancellationToken, maxChars),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
        return maxChars is { } budget ? CutPreview(text, budget) : text;
    }

    /// <summary>What a cut preview ends with: an ellipsis on a line of its own.</summary>
    public const string PreviewEllipsis = "\n…\n";

    /// <summary>
    /// A text longer than <paramref name="maxChars"/> is cut at the last line break at or before that length (at the length when there
    /// is none) and gets <see cref="PreviewEllipsis"/>; a shorter text is returned as it is. The formatters stop building once their text is
    /// past the budget, so what they hand in here can be longer than the budget but never shorter than the cut point.
    /// </summary>
    internal static string CutPreview(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var end = text.LastIndexOf('\n', maxChars);
        return string.Concat(text.AsSpan(0, end > 0 ? end : maxChars), PreviewEllipsis);
    }

    /// <summary>The build has produced more than the budget and there is more to come: stop.</summary>
    private static bool OverBudget(StringBuilder text, int? maxChars) => maxChars is { } budget && text.Length > budget;

    /// <summary>
    /// A minimal valid .docx (WordprocessingML in a zip: <c>[Content_Types].xml</c>, <c>_rels/.rels</c>, <c>word/document.xml</c>,
    /// <c>word/styles.xml</c> and the relationship part that ties the styles to the document) with the same content as the Markdown
    /// export: headings, paragraphs, bullet lists. Hand-written, no dependency; the same recording gives the same bytes.
    /// </summary>
    public static byte[] ToDocx(
        SessionRecord session,
        Transcript transcript,
        SessionSummary? summary,
        ExportLabels? labels,
        ExportOptions options,
        ExportRange? range)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(options);
        labels ??= ExportLabels.English;
        var paragraphs = new List<DocxParagraph>
        {
            new(DocxWriter.Heading1, [new DocxRun(TitleOf(session, labels))]),
            new(DocxWriter.Meta, [new DocxRun(MetaLine(session, transcript))]),
        };

        var summaryText = HasSummaryText(summary, options);
        if (summaryText)
        {
            paragraphs.Add(new(DocxWriter.Heading2, [new DocxRun(labels.Summary)]));
            if (!string.IsNullOrWhiteSpace(summary!.Overview))
            {
                paragraphs.Add(new(DocxWriter.Heading3, [new DocxRun(labels.Overview)]));
                paragraphs.Add(new(DocxWriter.Normal, [new DocxRun(summary.Overview.Trim())]));
            }

            AddBullets(paragraphs, DocxWriter.Heading3, labels.KeyPoints, summary.KeyPoints, "•", options.Timestamps);
        }

        if (HasActionItems(summary, options))
        {
            // Under the Summary heading when there is one, else a section of its own (no level is skipped).
            AddBullets(paragraphs, summaryText ? DocxWriter.Heading3 : DocxWriter.Heading2, labels.ActionItems, summary!.ActionItems, "☐", options.Timestamps);
        }

        paragraphs.Add(new(DocxWriter.Heading2, [new DocxRun(labels.Transcript)]));
        foreach (var segment in Segments(transcript, range))
        {
            var runs = new List<DocxRun>(3);
            if (options.SpeakerNames)
            {
                runs.Add(new DocxRun(SpeakerOf(segment, labels), Bold: true));
            }

            if (options.Timestamps)
            {
                runs.Add(new DocxRun((runs.Count > 0 ? " " : string.Empty) + Clock(segment.Start), Mono: true));
            }

            runs.Add(new DocxRun(OneLine(segment.Text), BreakBefore: runs.Count > 0));
            paragraphs.Add(new(DocxWriter.Normal, runs));
        }

        return DocxWriter.Build(paragraphs);
    }

    private static void AddBullets(List<DocxParagraph> paragraphs, string headingStyle, string heading, IReadOnlyList<SummaryItem> items, string bullet, bool timestamps)
    {
        if (items.Count == 0)
        {
            return;
        }

        paragraphs.Add(new(headingStyle, [new DocxRun(heading)]));
        foreach (var item in items)
        {
            var runs = new List<DocxRun>(2) { new(bullet + "\t" + OneLine(item.Text)) };
            if (timestamps && item.At is { } at)
            {
                runs.Add(new DocxRun(" (" + Clock(at) + ")", Mono: true));
            }

            paragraphs.Add(new(DocxWriter.Bullet, runs));
        }
    }

    /// <summary>
    /// <c># Title</c>, a line with the date, length and language, the summary (overview, key points, action items; each item with its
    /// time) when <paramref name="summary"/> is given, then the transcript, one paragraph per segment with the speaker and the time.
    /// </summary>
    public static string ToMarkdown(SessionRecord session, Transcript transcript, SessionSummary? summary = null, ExportLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transcript);
        return Markdown(session, transcript, summary, labels ?? ExportLabels.English, ExportOptions.Default, null, CancellationToken.None, escape: true);
    }

    /// <summary>The title and the meta line, then one line per segment: <c>[12:21] You: text</c>.</summary>
    public static string ToPlainText(SessionRecord session, Transcript transcript, ExportLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transcript);
        return PlainText(session, transcript, null, labels ?? ExportLabels.English, ExportOptions.Default, null, CancellationToken.None);
    }

    /// <summary>SubRip: numbered cues <c>00:12:21,000 --> 00:12:29,000</c> with <c>Speaker: text</c>, one cue per segment.</summary>
    public static string ToSrt(SessionRecord session, Transcript transcript, ExportLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transcript);
        return Srt(transcript, labels ?? ExportLabels.English, ExportOptions.Default, null, CancellationToken.None);
    }

    /// <summary>WebVTT: the <c>WEBVTT - title</c> header, then cues <c>00:12:21.000 --> 00:12:29.000</c> with the speaker as a voice tag (<c>&lt;v You&gt;</c>).</summary>
    public static string ToWebVtt(SessionRecord session, Transcript transcript, ExportLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transcript);
        return WebVtt(session, transcript, labels ?? ExportLabels.English, ExportOptions.Default, null, CancellationToken.None);
    }

    private static string Markdown(SessionRecord session, Transcript transcript, SessionSummary? summary, ExportLabels labels, ExportOptions options, ExportRange? range, CancellationToken cancellationToken, bool escape, int? maxChars = null)
    {
        // Only the user's and the transcript's text is escaped: the title, summary, chapter-like items, speakers and lines; never the
        // headings, bullets, timestamps and meta line the exporter writes itself.
        var text = new StringBuilder();
        text.Append("# ").Append(Text(TitleOf(session, labels), escape)).Append("\n\n");
        text.Append(MetaLine(session, transcript)).Append("\n\n");

        var summaryText = HasSummaryText(summary, options);
        if (summaryText)
        {
            text.Append("## ").Append(labels.Summary).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(summary!.Overview))
            {
                text.Append("### ").Append(labels.Overview).Append("\n\n").Append(escape ? MarkdownText.EscapeBlock(summary.Overview) : summary.Overview.Trim()).Append("\n\n");
            }

            AppendItems(text, "### ", labels.KeyPoints, summary.KeyPoints, "- ", options.Timestamps, escape);
        }

        if (HasActionItems(summary, options))
        {
            // Under the Summary heading when there is one, else a section of its own (no level is skipped).
            AppendItems(text, summaryText ? "### " : "## ", labels.ActionItems, summary!.ActionItems, "- [ ] ", options.Timestamps, escape);
        }

        text.Append("## ").Append(labels.Transcript).Append("\n\n");
        foreach (var segment in Segments(transcript, range))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OverBudget(text, maxChars))
            {
                // Another segment follows: the full text is longer than the budget and starts with what is built.
                return text.ToString();
            }

            if (options.SpeakerNames)
            {
                text.Append("**").Append(Text(SpeakerOf(segment, labels), escape)).Append("**");
            }

            if (options.Timestamps)
            {
                text.Append(options.SpeakerNames ? " `" : "`").Append(Clock(segment.Start)).Append('`');
            }

            if (options.SpeakerNames || options.Timestamps)
            {
                text.Append("  \n");
            }

            text.Append(Text(segment.Text, escape)).Append("\n\n");
        }

        return text.ToString().TrimEnd('\n') + "\n";
    }

    private static string PlainText(SessionRecord session, Transcript transcript, SessionSummary? summary, ExportLabels labels, ExportOptions options, ExportRange? range, CancellationToken cancellationToken, int? maxChars = null)
    {
        var text = new StringBuilder();
        text.Append(TitleOf(session, labels)).Append('\n').Append(MetaLine(session, transcript)).Append("\n\n");
        var sections = false;
        if (HasSummaryText(summary, options))
        {
            sections = true;
            text.Append(labels.Summary.ToUpperInvariant()).Append('\n');
            if (!string.IsNullOrWhiteSpace(summary!.Overview))
            {
                text.Append(summary.Overview.Trim()).Append('\n');
            }

            text.Append('\n');
            AppendPlainItems(text, labels.KeyPoints, summary.KeyPoints, options.Timestamps);
        }

        if (HasActionItems(summary, options))
        {
            sections = true;
            AppendPlainItems(text, labels.ActionItems, summary!.ActionItems, options.Timestamps);
        }

        if (sections)
        {
            text.Append(labels.Transcript.ToUpperInvariant()).Append('\n');
        }

        foreach (var segment in Segments(transcript, range))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OverBudget(text, maxChars))
            {
                return text.ToString();
            }

            if (options.Timestamps)
            {
                text.Append('[').Append(Clock(segment.Start)).Append("] ");
            }

            if (options.SpeakerNames)
            {
                text.Append(SpeakerOf(segment, labels)).Append(": ");
            }

            text.Append(OneLine(segment.Text)).Append('\n');
        }

        return text.ToString();
    }

    private static string Srt(Transcript transcript, ExportLabels labels, ExportOptions options, ExportRange? range, CancellationToken cancellationToken, int? maxChars = null)
    {
        var text = new StringBuilder();
        var number = 1;
        foreach (var segment in Segments(transcript, range))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OverBudget(text, maxChars))
            {
                return text.ToString();
            }

            text.Append(number++.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append(CueTime(segment.Start, ',')).Append(" --> ").Append(CueTime(CueEnd(segment), ',')).Append('\n');
            if (options.SpeakerNames)
            {
                text.Append(SrtText(SpeakerOf(segment, labels))).Append(": ");
            }

            text.Append(SrtText(OneLine(segment.Text))).Append("\n\n");
        }

        return text.ToString();
    }

    private static string WebVtt(SessionRecord session, Transcript transcript, ExportLabels labels, ExportOptions options, ExportRange? range, CancellationToken cancellationToken, int? maxChars = null)
    {
        var text = new StringBuilder("WEBVTT - ").Append(HeaderText(TitleOf(session, labels))).Append("\n\n");
        foreach (var segment in Segments(transcript, range))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OverBudget(text, maxChars))
            {
                return text.ToString();
            }

            text.Append(CueTime(segment.Start, '.')).Append(" --> ").Append(CueTime(CueEnd(segment), '.')).Append('\n');
            if (options.SpeakerNames)
            {
                text.Append("<v ").Append(EscapeVoice(SpeakerOf(segment, labels))).Append('>');
            }

            text.Append(EscapeCue(OneLine(segment.Text)).Replace("-->", "--&gt;", StringComparison.Ordinal)).Append("\n\n");
        }

        return text.ToString();
    }

    /// <summary>The summary's own text (overview, key points) is asked for and there is some: only then is there a "Summary" section.</summary>
    private static bool HasSummaryText(SessionSummary? summary, ExportOptions options) =>
        options.Summary && summary is not null && (!string.IsNullOrWhiteSpace(summary.Overview) || summary.KeyPoints.Count > 0);

    private static bool HasActionItems(SessionSummary? summary, ExportOptions options) =>
        options.ActionItems && summary is { ActionItems.Count: > 0 };

    /// <summary>The segments that overlap <c>[Start, End)</c> of the range (a segment without length counts as a point); all of them without a range, none for a range that ends at or before its start.</summary>
    private static IEnumerable<TranscriptSegment> Segments(Transcript transcript, ExportRange? range)
    {
        if (range is not { } part)
        {
            return transcript.Segments;
        }

        if (part.End <= part.Start)
        {
            return [];
        }

        return transcript.Segments.Where(segment =>
            segment.Start < part.End && (segment.End > segment.Start ? segment.End > part.Start : segment.Start >= part.Start));
    }

    private static void AppendPlainItems(StringBuilder text, string heading, IReadOnlyList<SummaryItem> items, bool timestamps)
    {
        if (items.Count == 0)
        {
            return;
        }

        text.Append(heading.ToUpperInvariant()).Append('\n');
        foreach (var item in items)
        {
            text.Append("• ").Append(OneLine(item.Text));
            if (timestamps && item.At is { } at)
            {
                text.Append(" (").Append(Clock(at)).Append(')');
            }

            text.Append('\n');
        }

        text.Append('\n');
    }

    private static void AppendItems(StringBuilder text, string headingMark, string heading, IReadOnlyList<SummaryItem> items, string bullet, bool timestamps, bool escape)
    {
        if (items.Count == 0)
        {
            return;
        }

        text.Append(headingMark).Append(heading).Append("\n\n");
        foreach (var item in items)
        {
            text.Append(bullet).Append(Text(item.Text, escape));
            if (timestamps && item.At is { } at)
            {
                text.Append(" (`").Append(Clock(at)).Append("`)");
            }

            text.Append('\n');
        }

        text.Append('\n');
    }

    /// <summary>A user's or the transcript's text on one line, escaped for Markdown (<see cref="MarkdownText"/>) unless it is not going into Markdown.</summary>
    private static string Text(string text, bool escape) => escape ? MarkdownText.Escape(text) : OneLine(text);

    private static string TitleOf(SessionRecord session, ExportLabels labels) =>
        string.IsNullOrWhiteSpace(session.Title) ? labels.Untitled : OneLine(session.Title);

    /// <summary><c>2026-10-18 10:30 · 47:12 · en · parakeet</c>: the date as stored (local time of the recording), the length, the language and the engine when known.</summary>
    private static string MetaLine(SessionRecord session, Transcript transcript)
    {
        var line = new StringBuilder();
        line.Append(session.StartedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        if (session.Duration > TimeSpan.Zero)
        {
            line.Append(" · ").Append(Clock(session.Duration));
        }

        if (!string.IsNullOrWhiteSpace(transcript.Language))
        {
            line.Append(" · ").Append(transcript.Language);
        }

        if (!string.IsNullOrWhiteSpace(transcript.Engine))
        {
            line.Append(" · ").Append(transcript.Engine);
        }

        return line.ToString();
    }

    private static string SpeakerOf(TranscriptSegment segment, ExportLabels labels) =>
        segment.Speaker == Speaker.Me ? labels.Me : labels.Others;

    /// <summary>The end of a cue: the segment's end, at least one second after its start (a cue that ends before it starts is invalid).</summary>
    private static TimeSpan CueEnd(TranscriptSegment segment) =>
        segment.End > segment.Start ? segment.End : segment.Start + TimeSpan.FromSeconds(1);

    /// <summary><c>12:21</c> below an hour, <c>1:02:03</c> from an hour on.</summary>
    internal static string Clock(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        var total = (long)time.TotalSeconds;
        var hours = total / 3600;
        var minutes = total / 60 % 60;
        var seconds = total % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:D2}:{seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes:D2}:{seconds:D2}");
    }

    /// <summary><c>HH:MM:SS</c> + separator + milliseconds (SRT uses a comma, WebVTT a dot).</summary>
    private static string CueTime(TimeSpan time, char separator)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        var milliseconds = (long)time.TotalMilliseconds;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{milliseconds / 3_600_000:D2}:{milliseconds / 60_000 % 60:D2}:{milliseconds / 1000 % 60:D2}{separator}{milliseconds % 1000:D3}");
    }

    /// <summary>Line breaks inside a text would end a cue or a paragraph early: collapsed to single spaces.</summary>
    private static string OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string EscapeCue(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>The text of the WEBVTT header line may not contain "-->" (the parser would take the line for a cue timing).</summary>
    private static string HeaderText(string text) => text.Replace("-->", "->", StringComparison.Ordinal);

    /// <summary>SubRip players read &lt;i&gt;, &lt;b&gt; and &lt;font&gt; as markup: the angle brackets of the speech are dropped.</summary>
    private static string SrtText(string text) => text.Replace("<", string.Empty, StringComparison.Ordinal).Replace(">", string.Empty, StringComparison.Ordinal);

    private static string EscapeVoice(string name) => EscapeCue(name).Replace(".", string.Empty, StringComparison.Ordinal);
}

/// <summary>The formats of an export (<see cref="TranscriptExport"/>).</summary>
public enum ExportFormat
{
    Markdown,
    PlainText,
    Srt,
    WebVtt,

    /// <summary>Word: see <see cref="TranscriptExport.ToDocx"/>.</summary>
    Docx,
}
