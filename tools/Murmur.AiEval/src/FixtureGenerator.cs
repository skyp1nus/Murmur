using System.Globalization;
using System.Text;
using System.Text.Json;
using Murmur.Core.Transcription;

namespace Murmur.AiEval;

/// <summary>
/// Deterministically builds the ~60-minute meeting fixture: filler talk from <see cref="FixtureContent"/> with the
/// planted facts at their exact times, segments of 3–15 s, words spread evenly over each segment. No LLM involved;
/// the same seed always yields byte-identical files.
/// </summary>
public static class FixtureGenerator
{
    public const ulong DefaultSeed = 20261018;
    public const string FixtureFileName = "meeting-60min.json";
    public const string QuestionsFileName = "meeting-60min.questions.json";
    public const int ToleranceSeconds = 60;

    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(60);
    public const long MinSegmentMs = 3_000;
    public const long MaxSegmentMs = 15_000;

    // Conversational Ukrainian is about 2.6 words per second.
    private const long MsPerWord = 380;
    private const long MinGapMs = 150;
    private const long MaxGapMs = 1_100;

    public static FixtureTranscript GenerateTranscript(ulong seed = DefaultSeed)
    {
        var rng = new SplitMix64(seed);
        var segments = new List<FixtureSegment>(700);
        var speaker = Speaker.Me;
        var cursor = 0L;

        cursor = Add(segments, Speaker.Me, cursor, SpokenDuration(FixtureContent.Opening), FixtureContent.Opening) + Gap(rng);
        foreach (var fact in FixtureContent.Facts.OrderBy(f => f.At))
        {
            var at = (long)fact.At.TotalMilliseconds;
            cursor = Fill(segments, rng, ref speaker, cursor, at);
            cursor = Add(segments, fact.Speaker, at, SpokenDuration(fact.Text), fact.Text) + Gap(rng);
            speaker = fact.Speaker;
        }

        var total = (long)Duration.TotalMilliseconds;
        var closingDuration = SpokenDuration(FixtureContent.Closing);
        var closingStart = total - closingDuration - 500;
        Fill(segments, rng, ref speaker, cursor, closingStart);
        Add(segments, Speaker.Me, closingStart, closingDuration, FixtureContent.Closing);

        return new FixtureTranscript
        {
            Title = FixtureContent.Title,
            Engine = "fixture",
            Language = "uk",
            DurationMs = total,
            Seed = seed,
            Segments = segments,
        };
    }

    public static QuestionSet GenerateQuestions()
    {
        var questions = new List<EvalQuestion>();
        foreach (var fact in FixtureContent.Facts)
        {
            if (fact.Question is null)
            {
                continue;
            }

            questions.Add(new EvalQuestion
            {
                Id = "q" + (questions.Count + 1).ToString("D2", CultureInfo.InvariantCulture),
                Question = fact.Question,
                Answerable = true,
                ExpectedMs = (long)fact.At.TotalMilliseconds,
                Expected = Label(fact.At),
                ToleranceSeconds = ToleranceSeconds,
                Keywords = fact.Keywords ?? throw new InvalidOperationException($"Fact {fact.Id} has a question but no keywords."),
                Fact = fact.Text,
                FactStems = fact.AllAnswerStems,
            });
        }

        foreach (var unanswerable in FixtureContent.Unanswerable)
        {
            questions.Add(new EvalQuestion
            {
                Id = "q" + (questions.Count + 1).ToString("D2", CultureInfo.InvariantCulture),
                Question = unanswerable.Question,
                Answerable = false,
                ToleranceSeconds = ToleranceSeconds,
                Keywords = unanswerable.Keywords,
            });
        }

        return new QuestionSet { Fixture = FixtureFileName, Questions = questions };
    }

    /// <summary>Indented JSON with one segment per line, LF line endings and readable Cyrillic.</summary>
    public static string SerializeTranscript(FixtureTranscript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        var json = new StringBuilder(1 << 20);
        json.Append("{\n");
        json.Append("  \"title\": ").Append(JsonSerializer.Serialize(transcript.Title, EvalJsonContext.Compact.String)).Append(",\n");
        json.Append("  \"engine\": ").Append(JsonSerializer.Serialize(transcript.Engine, EvalJsonContext.Compact.String)).Append(",\n");
        if (transcript.Language is { } language)
        {
            json.Append("  \"language\": ").Append(JsonSerializer.Serialize(language, EvalJsonContext.Compact.String)).Append(",\n");
        }

        json.Append("  \"durationMs\": ").Append(transcript.DurationMs.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        json.Append("  \"seed\": ").Append(transcript.Seed.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        json.Append("  \"segments\": [\n");
        for (var i = 0; i < transcript.Segments.Count; i++)
        {
            json.Append("    ").Append(JsonSerializer.Serialize(transcript.Segments[i], EvalJsonContext.Compact.FixtureSegment));
            json.Append(i + 1 < transcript.Segments.Count ? ",\n" : "\n");
        }

        json.Append("  ]\n}\n");
        return json.ToString();
    }

    public static string SerializeQuestions(QuestionSet questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        return JsonSerializer.Serialize(questions, EvalJsonContext.Readable.QuestionSet).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>Writes both fixture files into <paramref name="directory"/>; returns their paths.</summary>
    public static (string Fixture, string Questions) WriteFiles(string directory, ulong seed = DefaultSeed)
    {
        Directory.CreateDirectory(directory);
        var fixture = Path.Combine(directory, FixtureFileName);
        var questions = Path.Combine(directory, QuestionsFileName);
        File.WriteAllText(fixture, SerializeTranscript(GenerateTranscript(seed)), new UTF8Encoding(false));
        File.WriteAllText(questions, SerializeQuestions(GenerateQuestions()), new UTF8Encoding(false));
        return (fixture, questions);
    }

    /// <summary><c>mm:ss</c> below an hour, <c>h:mm:ss</c> above (same as the assistant's citation labels).</summary>
    public static string Label(TimeSpan time)
    {
        var total = (long)time.TotalSeconds;
        return total >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{total / 3600}:{total / 60 % 60:D2}:{total % 60:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{total / 60:D2}:{total % 60:D2}");
    }

    /// <summary>Filler segments from <paramref name="cursor"/> until just before <paramref name="limit"/>; returns <paramref name="limit"/>.</summary>
    private static long Fill(List<FixtureSegment> segments, SplitMix64 rng, ref Speaker speaker, long cursor, long limit)
    {
        while (true)
        {
            var maxDuration = Math.Min(MaxSegmentMs, limit - MinGapMs - cursor);
            if (maxDuration < MinSegmentMs)
            {
                // A short pause before the next planned segment.
                return limit;
            }

            if (rng.NextDouble() < 0.6)
            {
                speaker = speaker == Speaker.Me ? Speaker.Others : Speaker.Me;
            }

            var (text, duration) = Filler(rng, maxDuration);
            cursor = Add(segments, speaker, cursor, duration, text) + Gap(rng);
            if (cursor >= limit)
            {
                return limit;
            }
        }
    }

    /// <summary>Filler text that fits into <paramref name="maxDuration"/> (at least <see cref="MinSegmentMs"/>).</summary>
    private static (string Text, long Duration) Filler(SplitMix64 rng, long maxDuration)
    {
        if (rng.NextDouble() < 0.1)
        {
            // A short standalone reply.
            return (Pick(rng, FixtureContent.Reactions), MinSegmentMs + rng.Next(0, 800));
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            // Mostly short turns, sometimes a longer explanation.
            var target = rng.NextDouble() < 0.06 ? rng.Next(22, 33) : rng.Next(4, 15);
            var text = new StringBuilder();
            var words = 0;
            if (rng.NextDouble() < 0.25)
            {
                words += AppendSentence(text, Pick(rng, FixtureContent.Reactions));
            }

            while (words < target)
            {
                words += AppendSentence(text, Expand(rng, Pick(rng, FixtureContent.Sentences)));
            }

            var duration = Math.Clamp((words * MsPerWord) + rng.Next(-300, 301), MinSegmentMs, MaxSegmentMs);
            if (duration <= maxDuration)
            {
                return (text.ToString(), duration);
            }
        }

        // Nothing fits: a short reaction stretched over the remaining time (a slow, hesitant reply).
        var reaction = Pick(rng, FixtureContent.Reactions);
        var upper = Math.Min(maxDuration, MinSegmentMs + 1_500);
        return (reaction, MinSegmentMs + rng.Next(0, (int)(upper - MinSegmentMs) + 1));
    }

    private static int AppendSentence(StringBuilder text, string sentence)
    {
        if (text.Length > 0)
        {
            text.Append(' ');
        }

        text.Append(sentence);
        return CountWords(sentence);
    }

    private static string Expand(SplitMix64 rng, string template)
    {
        var result = new StringBuilder(template.Length + 32);
        Person? person = null;
        for (var i = 0; i < template.Length; i++)
        {
            var close = template[i] == '{' ? template.IndexOf('}', i) : -1;
            if (close < 0)
            {
                result.Append(template[i]);
                continue;
            }

            var key = template.Substring(i + 1, close - i - 1);
            if (key is "P" or "PG" or "PI")
            {
                person = Pick(rng, FixtureContent.People);
            }

            result.Append(key switch
            {
                "F" => Pick(rng, FixtureContent.Features).Nominative,
                "FA" => Pick(rng, FixtureContent.Features).Accusative,
                "FG" => Pick(rng, FixtureContent.Features).Genitive,
                "FL" => Pick(rng, FixtureContent.Features).AfterPo,
                "T" => Pick(rng, FixtureContent.Tools),
                "P" => person!.Nominative,
                "PG" => person!.Genitive,
                "PI" => person!.Instrumental,
                "V" => (person ?? throw new InvalidOperationException($"{{V}} before a person in: {template}")).PastEnding,
                "N" => rng.Next(5, 20).ToString(CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException($"Unknown placeholder {{{key}}} in: {template}"),
            });
            i = close;
        }

        return result.ToString();
    }

    private static T Pick<T>(SplitMix64 rng, IReadOnlyList<T> items) => items[rng.Next(0, items.Count)];

    private static long Gap(SplitMix64 rng) => rng.Next((int)MinGapMs, (int)MaxGapMs + 1);

    private static long SpokenDuration(string text) => Math.Clamp(CountWords(text) * MsPerWord, MinSegmentMs, MaxSegmentMs);

    private static int CountWords(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Appends a segment with evenly spread word timings; returns its end.</summary>
    private static long Add(List<FixtureSegment> segments, Speaker speaker, long start, long duration, string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words = new FixtureWord[tokens.Length];
        var slot = (double)duration / tokens.Length;
        var pause = (long)Math.Min(60, slot / 5);
        for (var i = 0; i < tokens.Length; i++)
        {
            var wordStart = start + (long)Math.Round(i * slot);
            var wordEnd = start + (long)Math.Round((i + 1) * slot) - pause;
            words[i] = new FixtureWord(tokens[i], wordStart, Math.Max(wordEnd, wordStart + 1));
        }

        segments.Add(new FixtureSegment { Speaker = speaker, StartMs = start, EndMs = start + duration, Text = text, Words = words });
        return start + duration;
    }
}

/// <summary>SplitMix64: tiny, fully specified PRNG, so the fixture never depends on <see cref="Random"/>'s algorithm.</summary>
internal sealed class SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    public int Next(int minInclusive, int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxExclusive, minInclusive);
        var range = (ulong)((long)maxExclusive - minInclusive);
        return (int)(minInclusive + (long)(NextUInt64() % range));
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));
}
