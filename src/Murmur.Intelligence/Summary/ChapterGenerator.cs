using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Murmur.Intelligence.Summary;

/// <summary>
/// <see cref="IChapterGenerator"/> over <see cref="ILlm"/>, built like <see cref="SessionSummarizer"/>: the transcript goes to the chat
/// model as timestamped lines (<c>[12:34] Me: text</c>). A transcript that fits <see cref="SummarizerOptions.ChunkCharacters"/> is split in one
/// request; a longer one is cut into chunks along segment boundaries, each chunk yields chapter candidates (map) and the candidates are merged into
/// the final list (reduce; when they are too many for one request they are first condensed in groups). The model answers one chapter per line,
/// <c>- [12:34] Title</c>; the parser snaps every time to the start of the transcript segment it falls in (<see cref="SessionSummarizer.Snap"/>),
/// puts the first chapter at 0, drops chapters that start too close to the previous one and merges the shortest ones when there are more than
/// <see cref="SummarizerOptions.MaxChapters"/>.
/// </summary>
public sealed partial class ChapterGenerator : IChapterGenerator
{
    private readonly ILlm _llm;
    private readonly ITranscriptStore _transcripts;
    private readonly SummarizerOptions _options;

    public ChapterGenerator(ILlm llm, ITranscriptStore transcripts, SummarizerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(transcripts);
        _llm = llm;
        _transcripts = transcripts;
        _options = options ?? new SummarizerOptions();
    }

    public TimeSpan MinimumDuration => _options.ChapterMinimumDuration;

    public async ValueTask<IReadOnlyList<Chapter>> GenerateChaptersAsync(SessionId session, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transcript = await _transcripts.GetTranscriptAsync(session, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session {session} has no transcript to split into chapters.");
        var lines = SessionSummarizer.BuildLines(transcript, _options.ChunkCharacters);
        if (lines.Count == 0)
        {
            throw new InvalidOperationException($"The transcript of session {session} has no speech to split into chapters.");
        }

        var spans = SessionSummarizer.SegmentSpans(transcript);
        var length = End(spans);
        if (length < MinimumDuration)
        {
            progress?.Report(1);
            return [];
        }

        var chunks = SessionSummarizer.Pack(lines, _options.ChunkCharacters);
        var reporter = new SessionSummarizer.ProgressReporter(progress, chunks.Count > 1 ? chunks.Count + 1 : 1);
        var language = SessionSummarizer.LanguageName(transcript.Language);
        var minutes = Math.Max(1, (int)Math.Round(length.TotalMinutes));
        var chat = await _llm.GetChatClientAsync(cancellationToken).ConfigureAwait(false);

        string answer;
        if (chunks.Count == 1)
        {
            answer = await AskAsync(chat, Prompts.System(language, _options), Prompts.Chapters(chunks[0], minutes, _options), cancellationToken).ConfigureAwait(false);
            reporter.Step();
        }
        else
        {
            var candidates = new List<string>(chunks.Count);
            for (var i = 0; i < chunks.Count; i++)
            {
                candidates.Add(await AskAsync(chat, Prompts.System(language, _options), Prompts.Candidates(chunks[i], i + 1, chunks.Count), cancellationToken).ConfigureAwait(false));
                reporter.Step();
            }

            // The candidates of a very long meeting can be longer than a request: condense them in groups until they fit.
            while (candidates.Count > 1 && SessionSummarizer.TotalLength(candidates) > _options.ReduceCharacters)
            {
                var groups = SessionSummarizer.Pack(candidates, _options.ReduceCharacters);
                if (groups.Count >= candidates.Count)
                {
                    break;
                }

                reporter.AddSteps(groups.Count);
                var condensed = new List<string>(groups.Count);
                for (var i = 0; i < groups.Count; i++)
                {
                    condensed.Add(await AskAsync(chat, Prompts.System(language, _options), Prompts.Condense(groups[i]), cancellationToken).ConfigureAwait(false));
                    reporter.Step();
                }

                candidates = condensed;
            }

            var merged = SessionSummarizer.Clip(string.Join("\n", candidates), _options.ReduceCharacters);
            answer = await AskAsync(chat, Prompts.System(language, _options), Prompts.Merge(merged, minutes, _options), cancellationToken).ConfigureAwait(false);
            reporter.Step();
        }

        var chapters = Resolve(ChapterParser.Parse(answer, _options.MaxChapterTitleLength), spans, length);
        if (chapters.Count < _options.MinChapters)
        {
            throw new InvalidDataException("The model returned no usable chapters.");
        }

        reporter.Complete();
        return chapters;
    }

    /// <summary>One request, non-streaming; the answer text trimmed.</summary>
    private async Task<string> AskAsync(IChatClient chat, string system, string prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = new ChatOptions
        {
            Temperature = _options.Temperature,
            MaxOutputTokens = _options.ChapterMaxOutputTokens,
            Reasoning = new ReasoningOptions { Effort = _options.Reasoning },
        };
        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, prompt)], options, cancellationToken)
            .ConfigureAwait(false);
        return response.Text.Trim();
    }

    /// <summary>The end of the last segment: how long the session talked.</summary>
    private static TimeSpan End(List<(TimeSpan Start, TimeSpan End)> spans)
    {
        var end = TimeSpan.Zero;
        foreach (var span in spans)
        {
            if (span.End > end)
            {
                end = span.End;
            }
        }

        return end;
    }

    /// <summary>Snaps the cited times to segment starts, orders and thins the chapters out, and puts the first one at 0.</summary>
    private List<Chapter> Resolve(List<ParsedChapter> parsed, List<(TimeSpan Start, TimeSpan End)> spans, TimeSpan length)
    {
        var snapped = new List<Chapter>(parsed.Count);
        foreach (var chapter in parsed)
        {
            if (SessionSummarizer.Snap(chapter.At, spans, _options.CitationTolerance) is { } start)
            {
                snapped.Add(new Chapter(chapter.Title, start));
            }
        }

        // Stable by time: two chapters on the same segment keep the model's order, and the first of them wins.
        var ordered = snapped.Select((chapter, index) => (chapter, index)).OrderBy(x => x.chapter.Start).ThenBy(x => x.index).Select(x => x.chapter).ToList();
        var result = new List<Chapter>(ordered.Count);
        foreach (var chapter in ordered)
        {
            if (result.Count == 0)
            {
                // The first chapter takes in everything before it: it starts at the very beginning.
                result.Add(chapter with { Start = TimeSpan.Zero });
                continue;
            }

            if (chapter.Start - result[^1].Start >= _options.MinChapterLength)
            {
                result.Add(chapter);
            }
        }

        while (result.Count > Math.Max(1, _options.MaxChapters))
        {
            // The shortest chapter (never the first) is merged into the one before it.
            var shortest = 1;
            var shortestLength = TimeSpan.MaxValue;
            for (var i = 1; i < result.Count; i++)
            {
                var next = i + 1 < result.Count ? result[i + 1].Start : length;
                var duration = next - result[i].Start;
                if (duration < shortestLength)
                {
                    shortest = i;
                    shortestLength = duration;
                }
            }

            result.RemoveAt(shortest);
        }

        return result;
    }

    private sealed record ParsedChapter(string Title, TimeSpan At);

    /// <summary>Reads the lines of the model's answer; tolerant of markdown, numbering and brackets.</summary>
    private static partial class ChapterParser
    {
        // The time at the start of a line, with or without brackets: [12:34], 12:34, [1:02:03], a copied range [12:00–12:45] (the start counts).
        [GeneratedRegex(@"^\**\s*\[?\s*(?:(\d+):)?(\d{1,2}):(\d{2})(?:\s*[–-]\s*(?:\d+:)?\d{1,2}:\d{2})?\s*\]?\**", RegexOptions.CultureInvariant)]
        private static partial Regex LeadingTimePattern();

        [GeneratedRegex(@"^\s*(?:[-*•–]|\d{1,2}[.)])\s+", RegexOptions.CultureInvariant)]
        private static partial Regex BulletPattern();

        // What the model copies from the form of the prompt: "<title>".
        [GeneratedRegex(@"^<[^<>]*>$", RegexOptions.CultureInvariant)]
        private static partial Regex PlaceholderPattern();

        [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
        private static partial Regex SpacesPattern();

        public static List<ParsedChapter> Parse(string answer, int maxTitleLength)
        {
            var chapters = new List<ParsedChapter>();
            foreach (var raw in answer.ReplaceLineEndings("\n").Split('\n'))
            {
                var line = BulletPattern().Replace(raw.Trim(), string.Empty);
                var match = LeadingTimePattern().Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var hours = match.Groups[1].Success ? int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture) : 0;
                var minutes = int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture);
                if (seconds >= 60 || (match.Groups[1].Success && minutes >= 60) || hours > 999)
                {
                    continue;
                }

                var title = Title(line[match.Length..], maxTitleLength);
                if (title.Length > 0 && !PlaceholderPattern().IsMatch(title))
                {
                    chapters.Add(new ParsedChapter(title, new TimeSpan(hours, minutes, seconds)));
                }
            }

            return chapters;
        }

        /// <summary>
        /// Plain one-line title: markdown, quotes and a leading dash or colon removed, spaces collapsed, no trailing period, cut at a word
        /// boundary to <paramref name="maxLength"/> characters.
        /// </summary>
        public static string Title(string text, int maxLength)
        {
            var cleaned = text.Replace("**", string.Empty, StringComparison.Ordinal).Replace("__", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
            cleaned = SpacesPattern().Replace(cleaned, " ").Trim(' ', '\t', '-', '–', '—', ':', '"', '\'', '“', '”', '«', '»');
            if (cleaned.Length > maxLength)
            {
                var cut = cleaned.LastIndexOf(' ', maxLength);
                if (cut < maxLength / 2)
                {
                    cut = maxLength;
                    if (char.IsHighSurrogate(cleaned[cut - 1]))
                    {
                        cut--;
                    }
                }

                cleaned = cleaned[..cut];
            }

            return cleaned.TrimEnd(' ', '.', '…', ',', ';', ':', '-', '–', '—', '"', '\'', '“', '”', '«', '»');
        }
    }

    /// <summary>The prompts. Model input, not UI text.</summary>
    private static class Prompts
    {
        private const string Format = "Reply with one chapter per line, in chronological order, in exactly this form and nothing else:\n- [mm:ss] <title>\n";

        public static string System(string language, SummarizerOptions options) =>
            "You split meeting transcripts into chapters for the Murmur recorder. The transcript lines look like \"[12:34] Me: text\" or \"[12:34] Others: text\": "
            + "\"Me\" is the user, \"Others\" are the other people on the call. Use only what the transcript says; invent nothing. "
            + (language.Length > 0
                ? "Write the chapter titles in " + language + ", the language of the transcript."
                : "Write the chapter titles in the language of the transcript (English or Ukrainian).")
            + " A title names the topic of the chapter in a few words (at most " + options.MaxChapterTitleLength.ToString(CultureInfo.InvariantCulture)
            + " characters), is not a sentence and has no period at the end.";

        public static string Chapters(string transcript, int minutes, SummarizerOptions options) =>
            "Split this meeting transcript into chapters: parts that are about one topic, roughly one chapter per 5 to 10 minutes. The meeting is "
            + minutes.ToString(CultureInfo.InvariantCulture) + " minutes long; make at least " + options.MinChapters.ToString(CultureInfo.InvariantCulture)
            + " and at most " + options.MaxChapters.ToString(CultureInfo.InvariantCulture) + " chapters. The first chapter starts at the first line.\n"
            + Rules() + Format + "\nTranscript:\n" + transcript;

        public static string Candidates(string chunk, int part, int parts) =>
            "This is part " + part.ToString(CultureInfo.InvariantCulture) + " of " + parts.ToString(CultureInfo.InvariantCulture)
            + " of a long meeting transcript. List where a new topic starts in this part only: roughly one chapter per 5 to 10 minutes of this part, at least one, the first at the first line of the part.\n"
            + Rules() + Format + "\nTranscript part:\n" + chunk;

        public static string Condense(string candidates) =>
            "These are chapter candidates from consecutive parts of one meeting. Condense them to about half as many: merge neighbours about the same topic, keep the time of the chapter you keep exactly as written.\n"
            + Format + "\nCandidates:\n" + candidates;

        public static string Merge(string candidates, int minutes, SummarizerOptions options) =>
            "These are chapter candidates from consecutive parts of one meeting that is " + minutes.ToString(CultureInfo.InvariantCulture)
            + " minutes long. Write the final chapters of the whole meeting from them: merge neighbours about the same topic, roughly one chapter per 5 to 10 minutes, at least "
            + options.MinChapters.ToString(CultureInfo.InvariantCulture) + " and at most " + options.MaxChapters.ToString(CultureInfo.InvariantCulture)
            + " chapters, the first at the earliest candidate. Keep the time of every chapter you keep exactly as written.\n"
            + Format + "\nCandidates:\n" + candidates;

        private static string Rules() =>
            "The time is the time of the transcript line where the chapter starts, copied exactly, e.g. [12:34] or [1:02:03].\n";
    }
}
