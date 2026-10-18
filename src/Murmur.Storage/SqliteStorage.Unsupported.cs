using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Storage;

// Transcript and search storage arrive in later phases; the schema gets new migrations then.
public sealed partial class SqliteStorage
{
    public ValueTask SaveTranscriptAsync(Transcript transcript, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Arrives in phase 2");

    public ValueTask<Transcript?> GetTranscriptAsync(SessionId id, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Arrives in phase 2");

    public ValueTask<IReadOnlyList<SearchHit>> SearchKeywordAsync(string query, SessionId? scope, int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Arrives in phase 2");

    public ValueTask ReplaceChunksAsync(SessionId session, IReadOnlyList<TextChunk> chunks, IReadOnlyList<ReadOnlyMemory<float>> embeddings, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Arrives in phase 3");

    public ValueTask<IReadOnlyList<SearchHit>> SearchVectorAsync(ReadOnlyMemory<float> embedding, SessionId? scope, int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Arrives in phase 3");
}
