using Microsoft.Extensions.AI;

namespace Murmur.Intelligence.Summary;

/// <summary>Chunking and generation settings of <see cref="SessionSummarizer"/>.</summary>
public sealed record SummarizerOptions
{
    /// <summary>
    /// Options whose chunks fit the context window of the model: the context minus the answer and the instructions, at 1.6 characters
    /// per token (Ukrainian text is 2 to 3 characters per token; the rest is safety margin).
    /// </summary>
    /// <param name="contextTokens">The context size the model server runs with (<c>LocalLlmOptions.ContextSize</c>).</param>
    public static SummarizerOptions ForContext(int contextTokens)
    {
        var template = new SummarizerOptions();
        var input = Math.Max(1_500, (int)((contextTokens - template.SummaryMaxOutputTokens - InstructionTokens) * CharactersPerToken));
        return template with { ChunkCharacters = input, ReduceCharacters = input };
    }

    /// <summary>Tokens of instructions and form around the text of a request.</summary>
    private const int InstructionTokens = 768;

    private const double CharactersPerToken = 1.6;

    /// <summary>
    /// Most characters of transcript sent in one request (about 3 000 tokens). A longer transcript is cut into chunks along
    /// segment boundaries, each summarised on its own (map), and the notes are merged (reduce).
    /// </summary>
    public int ChunkCharacters { get; init; } = 10_000;

    /// <summary>Most characters of notes merged in one request; more notes are first condensed in groups.</summary>
    public int ReduceCharacters { get; init; } = 10_000;

    /// <summary>The summary keeps at most this many key points.</summary>
    public int MaxKeyPoints { get; init; } = 8;

    /// <summary>The summary keeps at most this many action items.</summary>
    public int MaxActionItems { get; init; } = 10;

    /// <summary>A cited time resolves to a segment if it lies within the segment widened by this much on both sides (like the assistant's citations).</summary>
    public TimeSpan CitationTolerance { get; init; } = TimeSpan.FromSeconds(15);

    public float Temperature { get; init; } = 0.2f;

    /// <summary>Output budget of the notes of one chunk.</summary>
    public int NotesMaxOutputTokens { get; init; } = 768;

    /// <summary>Output budget of the final summary.</summary>
    public int SummaryMaxOutputTokens { get; init; } = 1024;

    /// <summary>Hidden reasoning is off: a summary of given text needs none, and on a CPU the thinking tokens take minutes.</summary>
    public ReasoningEffort Reasoning { get; init; } = ReasoningEffort.None;

    /// <summary>The id reported when the chat client does not tell its model.</summary>
    public string FallbackModelId { get; init; } = "local";
}
