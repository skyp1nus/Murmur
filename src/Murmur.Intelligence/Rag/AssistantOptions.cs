using Microsoft.Extensions.AI;

namespace Murmur.Intelligence.Rag;

/// <summary>Retrieval and generation settings of <see cref="Assistant"/>.</summary>
public sealed record AssistantOptions
{
    /// <summary>BM25 candidates fetched before fusion.</summary>
    public int KeywordCandidates { get; init; } = 20;

    /// <summary>Vector candidates fetched before fusion.</summary>
    public int VectorCandidates { get; init; } = 20;

    /// <summary>Reciprocal Rank Fusion constant: score = Σ 1 / (k + rank).</summary>
    public int RrfK { get; init; } = 60;

    /// <summary>Excerpts given to the model per question.</summary>
    public int AskExcerpts { get; init; } = 6;

    /// <summary>Chunks embedded per request while indexing.</summary>
    public int EmbeddingBatchSize { get; init; } = 16;

    /// <summary>Longest chunk produced while indexing.</summary>
    public TimeSpan ChunkMaxDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A cited time resolves to an excerpt if it lies within the excerpt widened by this much on both sides.</summary>
    public TimeSpan CitationTolerance { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Approximate length of a citation snippet.</summary>
    public int SnippetLength { get; init; } = 120;

    public float Temperature { get; init; } = 0.2f;

    public int MaxOutputTokens { get; init; } = 1024;

    /// <summary>
    /// Reasoning asked of the chat model. Off by default: an answer from a few excerpts needs no hidden thinking,
    /// and on a CPU the thinking tokens take minutes and can use up <see cref="MaxOutputTokens"/> before the answer.
    /// </summary>
    public ReasoningEffort Reasoning { get; init; } = ReasoningEffort.None;

    /// <summary>System prompt; the default is <see cref="Assistant.DefaultSystemPrompt"/>.</summary>
    public string? SystemPrompt { get; init; }
}
