using Murmur.Core.Transcription;

namespace Murmur.Core.Sessions;

public interface ISessionStore
{
    /// <summary>Creates the session row and its folder.</summary>
    ValueTask<SessionRecord> CreateSessionAsync(string title, DateTimeOffset startedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Writes every column of the row except the title (the title is the user's: <see cref="RenameSessionAsync"/> is the only way
    /// to change it, so a writer holding an older copy of the record — the recorder, the transcription pipeline — can never put an
    /// old title back). A session that does not exist throws <see cref="KeyNotFoundException"/>.
    /// </summary>
    ValueTask UpdateSessionAsync(SessionRecord session, CancellationToken cancellationToken);

    /// <summary>
    /// Changes the title of a session and nothing else (<c>UPDATE sessions SET title = $title WHERE id = $id</c>), so it cannot
    /// roll back the state, duration or warnings that another writer stored meanwhile. A session that does not exist throws
    /// <see cref="KeyNotFoundException"/>.
    /// </summary>
    ValueTask RenameSessionAsync(SessionId id, string title, CancellationToken cancellationToken);

    ValueTask<SessionRecord?> GetSessionAsync(SessionId id, CancellationToken cancellationToken);

    /// <summary>Newest first.</summary>
    ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(SessionQuery query, CancellationToken cancellationToken);

    /// <summary>Deletes the row, its transcript, its index entries and the session folder.</summary>
    ValueTask DeleteSessionAsync(SessionId id, CancellationToken cancellationToken);

    SessionFiles GetFiles(SessionId id);
}

public interface ITranscriptStore
{
    /// <summary>Replaces the transcript of a session and its full-text index.</summary>
    ValueTask SaveTranscriptAsync(Transcript transcript, CancellationToken cancellationToken);

    ValueTask<Transcript?> GetTranscriptAsync(SessionId id, CancellationToken cancellationToken);
}

/// <summary>A piece of transcript text positioned on a session timeline.</summary>
public sealed record TextChunk(SessionId Session, TimeSpan Start, TimeSpan End, string Text);

public enum SearchSource
{
    Keyword,
    Vector,
    Hybrid,
}

/// <summary>Higher <see cref="Score"/> is better regardless of <see cref="Source"/>.</summary>
public sealed record SearchHit(TextChunk Chunk, double Score, SearchSource Source);

public interface ISearchIndex
{
    /// <summary>BM25 search over transcript segments. <paramref name="scope"/> <c>null</c> searches the whole library.</summary>
    ValueTask<IReadOnlyList<SearchHit>> SearchKeywordAsync(string query, SessionId? scope, int limit, CancellationToken cancellationToken);

    /// <summary>Replaces the embedded chunks of a session. <paramref name="embeddings"/>[i] belongs to <paramref name="chunks"/>[i].</summary>
    ValueTask ReplaceChunksAsync(SessionId session, IReadOnlyList<TextChunk> chunks, IReadOnlyList<ReadOnlyMemory<float>> embeddings, CancellationToken cancellationToken);

    /// <summary>Cosine-similarity search over embedded chunks.</summary>
    ValueTask<IReadOnlyList<SearchHit>> SearchVectorAsync(ReadOnlyMemory<float> embedding, SessionId? scope, int limit, CancellationToken cancellationToken);
}

/// <summary>Everything persisted: session metadata, transcripts, search index and session files.</summary>
public interface IStorage : ISessionStore, ITranscriptStore, ISearchIndex
{
    /// <summary>Folder that contains the database and every session folder.</summary>
    string RootDirectory { get; }
}
