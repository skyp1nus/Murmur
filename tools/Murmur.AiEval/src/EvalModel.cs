using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.AiEval;

/// <summary>A fixture transcript (<c>tests/fixtures/meeting-60min.json</c>); times are integer milliseconds.</summary>
public sealed record FixtureTranscript
{
    public required string Title { get; init; }

    public required string Engine { get; init; }

    public string? Language { get; init; }

    public required long DurationMs { get; init; }

    public required ulong Seed { get; init; }

    public required IReadOnlyList<FixtureSegment> Segments { get; init; }

    /// <summary>The <see cref="Transcript"/> stored for <paramref name="session"/>.</summary>
    public Transcript ToTranscript(SessionId session)
    {
        var segments = new TranscriptSegment[Segments.Count];
        for (var i = 0; i < segments.Length; i++)
        {
            var s = Segments[i];
            var words = new TranscriptWord[s.Words.Count];
            for (var w = 0; w < words.Length; w++)
            {
                var word = s.Words[w];
                words[w] = new TranscriptWord(word.Text, TimeSpan.FromMilliseconds(word.StartMs), TimeSpan.FromMilliseconds(word.EndMs));
            }

            segments[i] = new TranscriptSegment(s.Speaker, TimeSpan.FromMilliseconds(s.StartMs), TimeSpan.FromMilliseconds(s.EndMs), s.Text, words);
        }

        return new Transcript(session, segments, Engine, Language);
    }
}

public sealed record FixtureSegment
{
    public required Speaker Speaker { get; init; }

    public required long StartMs { get; init; }

    public required long EndMs { get; init; }

    public required string Text { get; init; }

    public required IReadOnlyList<FixtureWord> Words { get; init; }
}

public sealed record FixtureWord(string Text, long StartMs, long EndMs);

/// <summary>The questions file (<c>tests/fixtures/meeting-60min.questions.json</c>).</summary>
public sealed record QuestionSet
{
    public required string Fixture { get; init; }

    public required IReadOnlyList<EvalQuestion> Questions { get; init; }
}

/// <summary>
/// One evaluation question. <see cref="Keywords"/> is what a user would type into the search box; it drives the
/// keyword-only retrieval check (BM25 ANDs every word, so a full natural-language question rarely matches a segment).
/// </summary>
public sealed record EvalQuestion
{
    public required string Id { get; init; }

    public required string Question { get; init; }

    public required bool Answerable { get; init; }

    /// <summary>Start of the segment that states the fact; <c>null</c> for unanswerable questions.</summary>
    public long? ExpectedMs { get; init; }

    /// <summary><see cref="ExpectedMs"/> as <c>mm:ss</c>, for humans.</summary>
    public string? Expected { get; init; }

    public int ToleranceSeconds { get; init; } = 60;

    public required string Keywords { get; init; }

    /// <summary>The planted sentence, for humans; <c>null</c> for unanswerable questions.</summary>
    public string? Fact { get; init; }

    /// <summary>
    /// Lower-case substrings of the fact, none of which occurs in <see cref="Question"/>. When only the assistant's
    /// fallback citation lands on the fact, the answer must contain one of them to pass. Empty for unanswerable questions.
    /// </summary>
    public IReadOnlyList<string> FactStems { get; init; } = [];

    [JsonIgnore]
    public TimeSpan? ExpectedAt => ExpectedMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    [JsonIgnore]
    public TimeSpan Tolerance => TimeSpan.FromSeconds(ToleranceSeconds);
}

public enum EvalMode
{
    /// <summary>Real chat model: AskAsync per question, scored by the cited times.</summary>
    Full,

    /// <summary>No chat model; hybrid SearchAsync with the question text (needs the embedding model).</summary>
    RetrievalHybrid,

    /// <summary>No models at all; BM25 SearchAsync with the question's <see cref="EvalQuestion.Keywords"/>.</summary>
    RetrievalKeyword,
}

public sealed record EvalReport
{
    public required EvalMode Mode { get; init; }

    public required int Passed { get; init; }

    /// <summary>Scored questions (skipped ones excluded).</summary>
    public required int Total { get; init; }

    public required double Rate { get; init; }

    /// <summary>Full mode: passes whose matching timestamp the model wrote itself (no fallback needed).</summary>
    public int ExplicitPassed { get; init; }

    public required double Threshold { get; init; }

    public required bool Success { get; init; }

    public required string Fixture { get; init; }

    public required int Segments { get; init; }

    public required ModelFiles Models { get; init; }

    public required EvalTimings Timings { get; init; }

    public required MachineInfo Machine { get; init; }

    public required IReadOnlyList<QuestionResult> PerQuestion { get; init; }
}

public sealed record ModelFiles(ModelFile? Chat, ModelFile? Embedding, ModelFile? Server);

public sealed record ModelFile(string Name, string Path, long? Bytes);

public sealed record EvalTimings
{
    public long SaveMs { get; init; }

    /// <summary>IndexSessionAsync, including the embedding server start; 0 when not indexed (keyword mode).</summary>
    public long IndexMs { get; init; }

    public int Chunks { get; init; }

    /// <summary>Chat server start (model load) before the first question; 0 without a chat model.</summary>
    public long ChatLoadMs { get; init; }

    public long AvgAnswerMs { get; init; }

    public long MaxAnswerMs { get; init; }

    public long TotalMs { get; init; }
}

public sealed record MachineInfo(string Os, string Architecture, int Processors, string Runtime);

public sealed record QuestionResult
{
    public required string Id { get; init; }

    public required string Question { get; init; }

    public required bool Answerable { get; init; }

    public string? Expected { get; init; }

    public long? ExpectedMs { get; init; }

    public int ToleranceSeconds { get; init; }

    /// <summary>Text sent to search (retrieval modes) or to the assistant (full mode).</summary>
    public required string Query { get; init; }

    /// <summary>Cited times (full mode) or top hit ranges (retrieval modes), as labels.</summary>
    public required IReadOnlyList<string> Cited { get; init; }

    /// <summary>Full mode: the answer itself wrote at least one timestamp that resolved to an excerpt.</summary>
    public bool ExplicitCitation { get; init; }

    /// <summary>Full mode, answerable: the matching citation was written by the model, not the top-excerpt fallback.</summary>
    public bool MatchedExplicitly { get; init; }

    public bool? SaysNotFound { get; init; }

    /// <summary>Full mode, answerable: the answer states the fact (needed when only the fallback citation matches).</summary>
    public bool? ConveysFact { get; init; }

    public required bool Pass { get; init; }

    /// <summary>Not scored in this mode (e.g. unanswerable questions in hybrid retrieval: vector search always returns hits).</summary>
    public bool Skipped { get; init; }

    public long LatencyMs { get; init; }

    public long? FirstTokenMs { get; init; }

    /// <summary>Full mode: why generation stopped (<c>stop</c>, or <c>length</c> when the token budget ran out).</summary>
    public string? FinishReason { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    /// <summary>Full mode: characters of hidden reasoning the model produced before or around the answer.</summary>
    public int? ReasoningChars { get; init; }

    public required string Answer { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(FixtureTranscript))]
[JsonSerializable(typeof(FixtureSegment))]
[JsonSerializable(typeof(QuestionSet))]
[JsonSerializable(typeof(EvalReport))]
internal sealed partial class EvalJsonContext : JsonSerializerContext
{
    /// <summary>Context that keeps Cyrillic and apostrophes readable in the committed files (no <c>\uXXXX</c>).</summary>
    // Lazy: static initializers of the generated partial (Default) may run after ours.
    public static EvalJsonContext Readable => field ??= new(new JsonSerializerOptions(Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>Same as <see cref="Readable"/> but on one line (fixture segments are written one per line).</summary>
    public static EvalJsonContext Compact => field ??= new(new JsonSerializerOptions(Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    });
}
