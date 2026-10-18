using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Storage;

// Search arrives in phase 3 (keyword search on top of the segments_fts table, vectors later).
public sealed partial class SqliteStorage
{
    public ValueTask<IReadOnlyList<SearchHit>> SearchKeywordAsync(string query, SessionId? scope, int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Arrives in phase 3");

    public ValueTask ReplaceChunksAsync(SessionId session, IReadOnlyList<TextChunk> chunks, IReadOnlyList<ReadOnlyMemory<float>> embeddings, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Arrives in phase 3");

    public ValueTask<IReadOnlyList<SearchHit>> SearchVectorAsync(ReadOnlyMemory<float> embedding, SessionId? scope, int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Arrives in phase 3");
}
