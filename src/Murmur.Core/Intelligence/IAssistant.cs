using Murmur.Core.Sessions;

namespace Murmur.Core.Intelligence;

public enum TurnRole
{
    User,
    Assistant,
}

public sealed record ChatTurn(TurnRole Role, string Text);

/// <param name="Scope">One session, or <c>null</c> for the whole library.</param>
public sealed record AskRequest(string Question, SessionId? Scope = null, IReadOnlyList<ChatTurn>? History = null);

/// <summary>A moment of a recording the answer refers to, e.g. <c>[12:34]</c>; clicking it opens the player there.</summary>
public sealed record Citation(SessionId Session, TimeSpan At, string Label, string Snippet);

/// <summary>
/// A streamed piece of an answer: text deltas while generating, and the resolved citations once,
/// with <see cref="IsFinal"/> set, at the end.
/// </summary>
public sealed record AssistantUpdate(string TextDelta, IReadOnlyList<Citation> Citations, bool IsFinal);

/// <summary>Retrieval-augmented Q&amp;A and search over transcripts.</summary>
public interface IAssistant
{
    /// <summary>Chunks and embeds a transcribed session so it becomes searchable.</summary>
    ValueTask IndexSessionAsync(SessionId session, CancellationToken cancellationToken);

    /// <summary>Hybrid search: BM25 and vector results fused by rank.</summary>
    ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, SessionId? scope, int limit, CancellationToken cancellationToken);

    IAsyncEnumerable<AssistantUpdate> AskAsync(AskRequest request, CancellationToken cancellationToken);
}
