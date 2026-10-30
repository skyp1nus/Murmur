using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;
using Murmur.Intelligence.Rag;

namespace Murmur.Intelligence.Summary;

/// <summary>
/// <see cref="ISessionSummarizer"/> over <see cref="ILlm"/>: the transcript goes to the chat model as timestamped lines
/// (<c>[12:34] Me: text</c>). A transcript that fits <see cref="SummarizerOptions.ChunkCharacters"/> is summarised in one request;
/// a longer one is cut into chunks along segment boundaries, each chunk is turned into notes (map) and the notes are merged into
/// the summary (reduce; when the notes themselves are too long they are first condensed in groups). The model answers in plain
/// sections (<c>OVERVIEW:</c>, <c>KEY POINTS:</c>, <c>ACTION ITEMS:</c>) and cites times as <c>[12:34]</c>; the parser turns the
/// citations into <see cref="SummaryItem.At"/>, snapped to the start of the transcript segment they fall in, and drops times that
/// match no segment.
/// </summary>
public sealed partial class SessionSummarizer : ISessionSummarizer
{
    private const string MeLabel = "Me";
    private const string OthersLabel = "Others";

    private readonly ILlm _llm;
    private readonly ITranscriptStore _transcripts;
    private readonly SummarizerOptions _options;
    private readonly TimeProvider _time;

    public SessionSummarizer(ILlm llm, ITranscriptStore transcripts, SummarizerOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(transcripts);
        _llm = llm;
        _transcripts = transcripts;
        _options = options ?? new SummarizerOptions();
        _time = time ?? TimeProvider.System;
    }

    public async ValueTask<SessionSummary> SummarizeAsync(SessionId session, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transcript = await _transcripts.GetTranscriptAsync(session, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session {session} has no transcript to summarise.");
        var lines = BuildLines(transcript);
        if (lines.Count == 0)
        {
            throw new InvalidOperationException($"The transcript of session {session} has no speech to summarise.");
        }

        var chunks = Pack(lines, _options.ChunkCharacters);
        var reporter = new ProgressReporter(progress, chunks.Count > 1 ? chunks.Count + 1 : 1);
        var language = LanguageName(transcript.Language);
        var chat = await _llm.GetChatClientAsync(cancellationToken).ConfigureAwait(false);
        var modelId = chat.GetService<ChatClientMetadata>()?.DefaultModelId is { Length: > 0 } id ? id : _options.FallbackModelId;

        string answer;
        if (chunks.Count == 1)
        {
            answer = await AskAsync(chat, Prompts.System(language), Prompts.Summary(chunks[0], _options), _options.SummaryMaxOutputTokens, cancellationToken).ConfigureAwait(false);
            reporter.Step();
        }
        else
        {
            var notes = new List<string>(chunks.Count);
            for (var i = 0; i < chunks.Count; i++)
            {
                notes.Add(await AskAsync(chat, Prompts.System(language), Prompts.Notes(chunks[i], i + 1, chunks.Count), _options.NotesMaxOutputTokens, cancellationToken).ConfigureAwait(false));
                reporter.Step();
            }

            // The notes of a very long meeting can be longer than a request: merge them in groups until they fit.
            while (notes.Count > 1 && TotalLength(notes) > _options.ReduceCharacters)
            {
                var groups = Pack(notes, _options.ReduceCharacters);
                if (groups.Count >= notes.Count)
                {
                    break;
                }

                reporter.AddSteps(groups.Count);
                var condensed = new List<string>(groups.Count);
                for (var i = 0; i < groups.Count; i++)
                {
                    condensed.Add(await AskAsync(chat, Prompts.System(language), Prompts.Condense(groups[i]), _options.NotesMaxOutputTokens, cancellationToken).ConfigureAwait(false));
                    reporter.Step();
                }

                notes = condensed;
            }

            var merged = Clip(string.Join("\n\n", notes), _options.ReduceCharacters);
            answer = await AskAsync(chat, Prompts.System(language), Prompts.Merge(merged, _options), _options.SummaryMaxOutputTokens, cancellationToken).ConfigureAwait(false);
            reporter.Step();
        }

        var parsed = SummaryParser.Parse(answer);
        if (parsed.Overview.Length == 0 && parsed.KeyPoints.Count == 0 && parsed.ActionItems.Count == 0)
        {
            throw new InvalidDataException("The model returned no summary.");
        }

        var spans = SegmentSpans(transcript);
        reporter.Complete();
        return new SessionSummary(
            session,
            parsed.Overview,
            Resolve(parsed.KeyPoints, spans, _options.MaxKeyPoints),
            Resolve(parsed.ActionItems, spans, _options.MaxActionItems),
            modelId,
            _time.GetUtcNow());
    }

    /// <summary>One request, non-streaming; the answer text trimmed.</summary>
    private async Task<string> AskAsync(IChatClient chat, string system, string prompt, int maxOutputTokens, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = new ChatOptions
        {
            Temperature = _options.Temperature,
            MaxOutputTokens = maxOutputTokens,
            Reasoning = new ReasoningOptions { Effort = _options.Reasoning },
        };
        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, prompt)], options, cancellationToken)
            .ConfigureAwait(false);
        return response.Text.Trim();
    }

    /// <summary>The model's input: one line per segment, <c>[12:34] Me: text</c>; very long segments are cut at word boundaries.</summary>
    private List<string> BuildLines(Transcript transcript)
    {
        var lines = new List<string>(transcript.Segments.Count);
        foreach (var segment in transcript.Segments)
        {
            var text = segment.Text.ReplaceLineEndings(" ").Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var prefix = "[" + Timestamps.Format(segment.Start) + "] " + (segment.Speaker == Speaker.Me ? MeLabel : OthersLabel) + ": ";
            var room = Math.Max(200, _options.ChunkCharacters - prefix.Length);
            while (text.Length > room)
            {
                var cut = text.LastIndexOf(' ', room);
                if (cut <= 0)
                {
                    cut = room;
                }

                lines.Add(prefix + text[..cut].TrimEnd());
                text = text[cut..].TrimStart();
            }

            lines.Add(prefix + text);
        }

        return lines;
    }

    /// <summary>Greedy packing of whole items into groups of at most <paramref name="limit"/> characters (an item longer than the limit is a group of its own).</summary>
    private static List<string> Pack(List<string> items, int limit)
    {
        var groups = new List<string>();
        var current = new StringBuilder();
        foreach (var item in items)
        {
            if (current.Length > 0 && current.Length + 1 + item.Length > limit)
            {
                groups.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(item);
        }

        if (current.Length > 0)
        {
            groups.Add(current.ToString());
        }

        return groups;
    }

    private static int TotalLength(List<string> items)
    {
        var total = 0;
        foreach (var item in items)
        {
            total += item.Length;
        }

        return total;
    }

    private static string Clip(string text, int limit) => text.Length <= limit ? text : text[..limit];

    private static string LanguageName(string? code)
    {
        if (code is null)
        {
            return string.Empty;
        }

        var primary = code.AsSpan();
        var dash = primary.IndexOfAny('-', '_');
        if (dash > 0)
        {
            primary = primary[..dash];
        }

        if (primary.Equals("uk", StringComparison.OrdinalIgnoreCase))
        {
            return "Ukrainian";
        }

        return primary.Equals("en", StringComparison.OrdinalIgnoreCase) ? "English" : string.Empty;
    }

    private static List<(TimeSpan Start, TimeSpan End)> SegmentSpans(Transcript transcript)
    {
        var spans = new List<(TimeSpan, TimeSpan)>(transcript.Segments.Count);
        foreach (var segment in transcript.Segments)
        {
            if (!string.IsNullOrWhiteSpace(segment.Text))
            {
                spans.Add((segment.Start, segment.End > segment.Start ? segment.End : segment.Start));
            }
        }

        return spans;
    }

    private List<SummaryItem> Resolve(List<ParsedItem> items, List<(TimeSpan Start, TimeSpan End)> spans, int max)
    {
        var result = new List<SummaryItem>(Math.Min(items.Count, max));
        foreach (var item in items)
        {
            if (result.Count >= max)
            {
                break;
            }

            result.Add(new SummaryItem(item.Text, item.At is { } at ? Snap(at, spans, _options.CitationTolerance) : null));
        }

        return result;
    }

    /// <summary>
    /// The start of the segment a cited time belongs to. The label the model copied (<c>[12:21]</c>) has no fractions, so a segment whose
    /// label is that one is the line cited, even when another speaker's longer segment also contains the moment: the latest such segment wins.
    /// Only without one the time is placed by position: the segment containing it, else the nearest one within <paramref name="tolerance"/>
    /// (like the assistant's citations); <c>null</c> when the time is in no segment.
    /// </summary>
    internal static TimeSpan? Snap(TimeSpan at, IReadOnlyList<(TimeSpan Start, TimeSpan End)> spans, TimeSpan tolerance)
    {
        var second = Math.Floor(at.TotalSeconds);
        TimeSpan? labelled = null;
        foreach (var (start, _) in spans)
        {
            if (Math.Floor(start.TotalSeconds) == second && (labelled is null || start > labelled))
            {
                labelled = start;
            }
        }

        if (labelled is not null)
        {
            return labelled;
        }

        TimeSpan? best = null;
        var bestDistance = TimeSpan.MaxValue;
        foreach (var (start, end) in spans)
        {
            var distance = at < start ? start - at : at > end ? at - end : TimeSpan.Zero;
            if (distance > tolerance)
            {
                continue;
            }

            // The later of two overlapping segments (the two speakers) that both contain the time wins: it is the one just started.
            if (distance < bestDistance || (distance == bestDistance && best is { } current && start > current))
            {
                best = start;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>Progress in [0, 1] that never goes back, even when condensing steps are added on the way.</summary>
    private sealed class ProgressReporter(IProgress<double>? progress, int total)
    {
        private int _done;
        private int _total = total;
        private double _last;

        public void AddSteps(int count) => _total += count;

        public void Step()
        {
            _done++;
            Report(Math.Min(0.99, (double)_done / _total));
        }

        public void Complete() => Report(1);

        private void Report(double value)
        {
            if (progress is null)
            {
                return;
            }

            _last = Math.Max(_last, value);
            progress.Report(_last);
        }
    }

    internal sealed record ParsedItem(string Text, TimeSpan? At);

    internal sealed record ParsedSummary(string Overview, List<ParsedItem> KeyPoints, List<ParsedItem> ActionItems);

    /// <summary>Reads the sections of the model's answer; tolerant of markdown, numbering and case.</summary>
    internal static partial class SummaryParser
    {
        private enum Section
        {
            None,
            Overview,
            KeyPoints,
            ActionItems,
        }

        // [12:34], [1:02:03] and a copied range [12:00–12:45] (the start counts), anywhere in a text.
        [GeneratedRegex(@"\[\s*(?:(\d+):)?(\d{1,2}):(\d{2})(?:\s*[–-]\s*(?:\d+:)?\d{1,2}:\d{2})?\s*\]", RegexOptions.CultureInvariant)]
        private static partial Regex TimestampPattern();

        // The same at the very start of an item: the only place a citation is accepted.
        [GeneratedRegex(@"^\s*\[\s*(?:(\d+):)?(\d{1,2}):(\d{2})(?:\s*[–-]\s*(?:\d+:)?\d{1,2}:\d{2})?\s*\]", RegexOptions.CultureInvariant)]
        private static partial Regex LeadingTimestampPattern();

        // What the model copies from the form of the prompt: "<one point>", "[mm:ss] <who does what>".
        [GeneratedRegex(@"^\s*(?:\[\s*mm\s*:\s*ss\s*\]\s*)?<[^<>]*>\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
        private static partial Regex PlaceholderPattern();

        [GeneratedRegex(@"^\s*(?:[-*•–]|\d{1,2}[.)])\s+", RegexOptions.CultureInvariant)]
        private static partial Regex BulletPattern();

        [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
        private static partial Regex SpacesPattern();

        public static ParsedSummary Parse(string answer)
        {
            var overview = new StringBuilder();
            var preface = new StringBuilder();
            var headed = false;
            var keyPoints = new List<ParsedItem>();
            var actionItems = new List<ParsedItem>();
            var section = Section.None;
            foreach (var raw in answer.ReplaceLineEndings("\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (TryHeader(line, out var header, out var rest))
                {
                    headed = true;
                    section = header;
                    line = rest;
                    if (line.Length == 0)
                    {
                        continue;
                    }
                }

                switch (section)
                {
                    case Section.Overview:
                        AppendText(overview, line);
                        break;
                    case Section.None:
                        AppendText(preface, line);
                        break;
                    case Section.KeyPoints:
                        Add(keyPoints, line);
                        break;
                    case Section.ActionItems:
                        Add(actionItems, line);
                        break;
                }
            }

            // Text before the first heading is a preamble ("Here is the summary:") when there are headings, the overview when there are none.
            var text = headed ? overview.ToString() : preface.ToString();
            return new ParsedSummary(PlaceholderPattern().IsMatch(text) ? string.Empty : StripTimes(text), keyPoints, actionItems);
        }

        private static void AppendText(StringBuilder builder, string line)
        {
            var plain = Clean(BulletPattern().Replace(line, string.Empty));
            if (plain.Length > 0)
            {
                builder.Append(builder.Length > 0 ? " " : string.Empty).Append(plain);
            }
        }

        private static void Add(List<ParsedItem> items, string line)
        {
            var body = BulletPattern().Replace(line, string.Empty);
            TimeSpan? at = null;
            var match = LeadingTimestampPattern().Match(body);
            if (match.Success)
            {
                var hours = match.Groups[1].Success ? int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture) : 0;
                var minutes = int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture);
                if (seconds < 60 && (!match.Groups[1].Success || minutes < 60) && hours <= 999)
                {
                    at = new TimeSpan(hours, minutes, seconds);
                }
            }

            var text = StripTimes(body);
            if (text.Length > 0 && !IsNone(text) && !PlaceholderPattern().IsMatch(text))
            {
                items.Add(new ParsedItem(text, at));
            }
        }

        private static string StripTimes(string text) => Clean(TimestampPattern().Replace(text, " "));

        /// <summary>Plain text: markdown emphasis removed, spaces collapsed, dangling dashes and colons trimmed.</summary>
        private static string Clean(string text)
        {
            var cleaned = text.Replace("**", string.Empty, StringComparison.Ordinal).Replace("__", string.Empty, StringComparison.Ordinal);
            return SpacesPattern().Replace(cleaned, " ").Trim(' ', '\t', '-', '–', '—', ':', ',', ';');
        }

        /// <summary>"none", "-" or "n/a" in place of an empty section.</summary>
        private static bool IsNone(string text) =>
            text.Equals("none", StringComparison.OrdinalIgnoreCase) || text.Equals("n/a", StringComparison.OrdinalIgnoreCase) || text == "—";

        private static bool TryHeader(string line, out Section section, out string rest)
        {
            section = Section.None;
            rest = line;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var candidate = colon >= 0 ? line[..colon] : line;
            var name = candidate.Trim('#', '*', '_', ' ', '\t').ToUpperInvariant();
            section = name switch
            {
                "OVERVIEW" or "SUMMARY" or "ОГЛЯД" or "ПІДСУМОК" or "РЕЗЮМЕ" => Section.Overview,
                "KEY POINTS" or "KEY POINT" or "DECISIONS" or "КЛЮЧОВІ ПУНКТИ" or "КЛЮЧОВІ МОМЕНТИ" or "КЛЮЧОВІ ПОЛОЖЕННЯ" or "РІШЕННЯ" => Section.KeyPoints,
                "ACTION ITEMS" or "ACTION ITEM" or "ACTIONS" or "NEXT STEPS" or "ЗАВДАННЯ" or "ДІЇ" or "НАСТУПНІ КРОКИ" or "ЗАДАЧІ" => Section.ActionItems,
                _ => Section.None,
            };
            if (section == Section.None)
            {
                return false;
            }

            rest = colon >= 0 ? line[(colon + 1)..].Trim('*', '_', ' ', '\t') : string.Empty;
            return true;
        }
    }

    /// <summary>The prompts. Model input, not UI text.</summary>
    private static class Prompts
    {
        private const string Format =
            "Reply in exactly this form, with these three English headings and nothing before or after:\n"
            + "OVERVIEW:\n<two to four sentences>\n"
            + "KEY POINTS:\n- [mm:ss] <one point>\n"
            + "ACTION ITEMS:\n- [mm:ss] <who does what>\n";

        public static string System(string language) =>
            "You write meeting summaries for the Murmur recorder. The transcript lines look like \"[12:34] Me: text\" or \"[12:34] Others: text\": "
            + "\"Me\" is the user, \"Others\" are the other people on the call. Use only what the transcript says; invent nothing. "
            + (language.Length > 0
                ? "Write the summary in " + language + ", the language of the transcript."
                : "Write the summary in the language of the transcript (English or Ukrainian).");

        public static string Summary(string transcript, SummarizerOptions options) =>
            "Summarise this meeting transcript.\n" + Rules(options) + Format + "\nTranscript:\n" + transcript;

        public static string Notes(string chunk, int part, int parts) =>
            "This is part " + part.ToString(CultureInfo.InvariantCulture) + " of " + parts.ToString(CultureInfo.InvariantCulture)
            + " of a long meeting transcript. Take notes on this part only: an overview of one or two sentences, the key points and the action items.\n"
            + "Cite the time of the line a point comes from in square brackets, copied from the transcript, e.g. [12:34].\n"
            + Format + "\nTranscript part:\n" + chunk;

        public static string Condense(string notes) =>
            "These are notes on consecutive parts of one meeting. Condense them into shorter notes in the same form, keeping the time in square brackets of every point you keep.\n"
            + Format + "\nNotes:\n" + notes;

        public static string Merge(string notes, SummarizerOptions options) =>
            "These are notes on consecutive parts of one meeting, in order. Write the summary of the whole meeting from them.\n" + Rules(options) + Format + "\nNotes:\n" + notes;

        private static string Rules(SummarizerOptions options) =>
            "The overview says what the meeting was about and what was decided. Key points are the decisions and the most important facts (at most "
            + options.MaxKeyPoints.ToString(CultureInfo.InvariantCulture) + "). Action items are tasks somebody has to do, with the person if the transcript says who (at most "
            + options.MaxActionItems.ToString(CultureInfo.InvariantCulture) + "); leave the section empty if there are none. "
            + "Start every key point and action item with the time in square brackets of the transcript line it comes from, copied exactly, e.g. [12:34] or [1:02:03].\n";
    }
}
