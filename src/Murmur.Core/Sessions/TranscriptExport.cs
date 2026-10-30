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

/// <summary>
/// Pure formatters that turn a recording into the text of an export file: Markdown (with the summary when there is one), plain text,
/// SRT subtitles and WebVTT. Lines end with <c>\n</c> in every format, times use the invariant culture, so the output of the same
/// recording is the same on every machine. The caller writes the string as UTF-8.
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
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>Formats the recording as <paramref name="format"/> (<paramref name="summary"/> only matters for Markdown).</summary>
    public static string Format(ExportFormat format, SessionRecord session, Transcript transcript, SessionSummary? summary = null, ExportLabels? labels = null) =>
        format switch
        {
            ExportFormat.Markdown => ToMarkdown(session, transcript, summary, labels),
            ExportFormat.PlainText => ToPlainText(session, transcript, labels),
            ExportFormat.Srt => ToSrt(session, transcript, labels),
            ExportFormat.WebVtt => ToWebVtt(session, transcript, labels),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

    /// <summary>
    /// <c># Title</c>, a line with the date, length and language, the summary (overview, key points, action items; each item with its
    /// time) when <paramref name="summary"/> is given, then the transcript, one paragraph per segment with the speaker and the time.
    /// </summary>
    public static string ToMarkdown(SessionRecord session, Transcript transcript, SessionSummary? summary = null, ExportLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transcript);
        labels ??= ExportLabels.English;
        var text = new StringBuilder();
        text.Append("# ").Append(TitleOf(session, labels)).Append("\n\n");
        text.Append(MetaLine(session, transcript)).Append("\n\n");

        if (summary is not null)
        {
            text.Append("## ").Append(labels.Summary).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(summary.Overview))
            {
                text.Append("### ").Append(labels.Overview).Append("\n\n").Append(summary.Overview.Trim()).Append("\n\n");
            }

            AppendItems(text, labels.KeyPoints, summary.KeyPoints, "- ");
            AppendItems(text, labels.ActionItems, summary.ActionItems, "- [ ] ");
        }

        text.Append("## ").Append(labels.Transcript).Append("\n\n");
        foreach (var segment in transcript.Segments)
        {
            text.Append("**").Append(SpeakerOf(segment, labels)).Append("** `").Append(Clock(segment.Start)).Append("`  \n")
                .Append(OneLine(segment.Text)).Append("\n\n");
        }

        return text.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>The title and the meta line, then one line per segment: <c>[12:21] You: text</c>.</summary>
    public static string ToPlainText(SessionRecord session, Transcript transcript, ExportLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transcript);
        labels ??= ExportLabels.English;
        var text = new StringBuilder();
        text.Append(TitleOf(session, labels)).Append('\n').Append(MetaLine(session, transcript)).Append("\n\n");
        foreach (var segment in transcript.Segments)
        {
            text.Append('[').Append(Clock(segment.Start)).Append("] ").Append(SpeakerOf(segment, labels)).Append(": ")
                .Append(OneLine(segment.Text)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>SubRip: numbered cues <c>00:12:21,000 --> 00:12:29,000</c> with <c>Speaker: text</c>, one cue per segment.</summary>
    public static string ToSrt(SessionRecord session, Transcript transcript, ExportLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transcript);
        labels ??= ExportLabels.English;
        var text = new StringBuilder();
        var number = 1;
        foreach (var segment in transcript.Segments)
        {
            text.Append(number++.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append(CueTime(segment.Start, ',')).Append(" --> ").Append(CueTime(CueEnd(segment), ',')).Append('\n')
                .Append(SpeakerOf(segment, labels)).Append(": ").Append(OneLine(segment.Text)).Append("\n\n");
        }

        return text.ToString();
    }

    /// <summary>WebVTT: the <c>WEBVTT - title</c> header, then cues <c>00:12:21.000 --> 00:12:29.000</c> with the speaker as a voice tag (<c>&lt;v You&gt;</c>).</summary>
    public static string ToWebVtt(SessionRecord session, Transcript transcript, ExportLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transcript);
        labels ??= ExportLabels.English;
        var text = new StringBuilder("WEBVTT - ").Append(TitleOf(session, labels)).Append("\n\n");
        foreach (var segment in transcript.Segments)
        {
            text.Append(CueTime(segment.Start, '.')).Append(" --> ").Append(CueTime(CueEnd(segment), '.')).Append('\n')
                .Append("<v ").Append(EscapeVoice(SpeakerOf(segment, labels))).Append('>').Append(EscapeCue(OneLine(segment.Text))).Append("\n\n");
        }

        return text.ToString();
    }

    private static void AppendItems(StringBuilder text, string heading, IReadOnlyList<SummaryItem> items, string bullet)
    {
        if (items.Count == 0)
        {
            return;
        }

        text.Append("### ").Append(heading).Append("\n\n");
        foreach (var item in items)
        {
            text.Append(bullet).Append(OneLine(item.Text));
            if (item.At is { } at)
            {
                text.Append(" (`").Append(Clock(at)).Append("`)");
            }

            text.Append('\n');
        }

        text.Append('\n');
    }

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

    private static string EscapeVoice(string name) => EscapeCue(name).Replace(".", string.Empty, StringComparison.Ordinal);
}

/// <summary>The formats of an export (<see cref="TranscriptExport"/>).</summary>
public enum ExportFormat
{
    Markdown,
    PlainText,
    Srt,
    WebVtt,
}
