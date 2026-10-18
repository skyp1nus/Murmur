using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;

namespace Murmur.Storage;

// ISearchIndex: BM25 keyword search over segments_fts and brute-force cosine search over embedded chunks (D-017).
public sealed partial class SqliteStorage
{
    // Guards the vector cache and serializes chunk writes with cache loads, so a load can never publish stale rows.
    private readonly object _vectorLock = new();
    private readonly Dictionary<SessionId, SessionVectors> _vectorCache = [];
    private SessionVectors[]? _vectorSnapshot;
    private bool _vectorCacheComplete;

    /// <summary>
    /// BM25-ranked full-text search over transcript segments. The query is sanitized into a safe FTS5 expression:
    /// words are AND-ed, the last one matches as a prefix, operators and punctuation are plain separators.
    /// An empty query or a non-positive <paramref name="limit"/> yields no results. Score is <c>-bm25</c>.
    /// </summary>
    public ValueTask<IReadOnlyList<SearchHit>> SearchKeywordAsync(string query, SessionId? scope, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Run<IReadOnlyList<SearchHit>>(() => SearchKeyword(query, scope, limit), cancellationToken);
    }

    /// <summary>
    /// Replaces the embedded chunks of a session in one transaction. Vectors are L2-normalized before they are stored.
    /// Throws <see cref="ArgumentException"/> when the counts differ, a vector is empty, non-finite or of another
    /// dimension than its siblings, and <see cref="KeyNotFoundException"/> for an unknown session. An empty list
    /// removes the session's chunks.
    /// </summary>
    public ValueTask ReplaceChunksAsync(SessionId session, IReadOnlyList<TextChunk> chunks, IReadOnlyList<ReadOnlyMemory<float>> embeddings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(embeddings);
        return Run(() => ReplaceChunks(session, chunks, embeddings), cancellationToken);
    }

    /// <summary>
    /// Cosine similarity of <paramref name="embedding"/> against every embedded chunk (optionally of one session),
    /// best first. Chunks embedded with another dimension than the query are skipped (stale model); an empty,
    /// zero or non-finite query yields no results.
    /// </summary>
    public ValueTask<IReadOnlyList<SearchHit>> SearchVectorAsync(ReadOnlyMemory<float> embedding, SessionId? scope, int limit, CancellationToken cancellationToken)
    {
        // The query is copied up front: the caller may reuse its buffer as soon as the call returns.
        var query = embedding.ToArray();
        return Run<IReadOnlyList<SearchHit>>(() => SearchVector(query, scope, limit), cancellationToken);
    }

    /// <summary>Turns free user text into a safe FTS5 expression, or <see langword="null"/> when no searchable word is left.</summary>
    internal static string? BuildFtsQuery(string text)
    {
        // Every maximal run of letters/digits/combining marks becomes a quoted phrase token, so FTS5 operators
        // (AND, OR, NOT, NEAR), column filters, quotes, '-', '^', '*' and parentheses can never reach the parser.
        StringBuilder? builder = null;
        var tokenStart = -1;
        for (var i = 0; i <= text.Length;)
        {
            var width = 1;
            var isTokenChar = false;
            if (i < text.Length)
            {
                // Lone or invalid surrogates (pasted/IME text) are treated as separators instead of throwing.
                if (Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out width) == OperationStatus.Done)
                {
                    isTokenChar = Rune.IsLetterOrDigit(rune) || IsCombiningMark(rune);
                }
                else
                {
                    width = Math.Max(width, 1);
                }
            }

            if (isTokenChar)
            {
                if (tokenStart < 0)
                {
                    tokenStart = i;
                }
            }
            else if (tokenStart >= 0)
            {
                builder ??= new StringBuilder(text.Length + 8);
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append('"').Append(text, tokenStart, i - tokenStart).Append('"');
                tokenStart = -1;
            }

            i += width;
        }

        if (builder is null)
        {
            return null;
        }

        return builder.Append('*').ToString();
    }

    private static bool IsCombiningMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    private List<SearchHit> SearchKeyword(string query, SessionId? scope, int limit)
    {
        var hits = new List<SearchHit>();
        var fts = BuildFtsQuery(query);
        if (fts is null || limit <= 0)
        {
            return hits;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.session_id, s.start_ms, s.end_ms, s.text, bm25(segments_fts) " +
            "FROM segments_fts JOIN segments s ON s.id = segments_fts.rowid " +
            "WHERE segments_fts MATCH $query" + (scope is null ? string.Empty : " AND s.session_id = $session") + " " +
            "ORDER BY bm25(segments_fts), s.session_id, s.idx LIMIT $limit";
        command.Parameters.AddWithValue("$query", fts);
        command.Parameters.AddWithValue("$limit", limit);
        if (scope is { } session)
        {
            command.Parameters.AddWithValue("$session", session.ToString());
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new SearchHit(
                new TextChunk(
                    SessionId.Parse(reader.GetString(0)),
                    TimeSpan.FromMilliseconds(reader.GetInt64(1)),
                    TimeSpan.FromMilliseconds(reader.GetInt64(2)),
                    reader.GetString(3)),
                -reader.GetDouble(4),
                SearchSource.Keyword));
        }

        return hits;
    }

    private void ReplaceChunks(SessionId session, IReadOnlyList<TextChunk> chunks, IReadOnlyList<ReadOnlyMemory<float>> embeddings)
    {
        if (chunks.Count != embeddings.Count)
        {
            throw new ArgumentException(
                $"{chunks.Count} chunks but {embeddings.Count} embeddings were given.", nameof(embeddings));
        }

        var dim = chunks.Count == 0 ? 0 : embeddings[0].Length;
        if (chunks.Count > 0 && dim == 0)
        {
            throw new ArgumentException("Embeddings must not be empty.", nameof(embeddings));
        }

        // Validate and normalize into the contiguous array that becomes the cache entry.
        var data = new float[checked(chunks.Count * dim)];
        var infos = new ChunkInfo[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            var embedding = embeddings[i];
            if (embedding.Length != dim)
            {
                throw new ArgumentException(
                    $"Embedding {i} has {embedding.Length} dimensions but embedding 0 has {dim}.", nameof(embeddings));
            }

            var row = data.AsSpan(i * dim, dim);
            embedding.Span.CopyTo(row);
            if (!VectorSearch.TryNormalize(row))
            {
                throw new ArgumentException($"Embedding {i} contains NaN or infinity.", nameof(embeddings));
            }

            var chunk = chunks[i] ?? throw new ArgumentException($"Chunk {i} is null.", nameof(chunks));
            infos[i] = new ChunkInfo((long)chunk.Start.TotalMilliseconds, (long)chunk.End.TotalMilliseconds, chunk.Text);
        }

        lock (_vectorLock)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);

            using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = "SELECT 1 FROM sessions WHERE id = $id";
                exists.Parameters.AddWithValue("$id", session.ToString());
                if (exists.ExecuteScalar() is null)
                {
                    throw new KeyNotFoundException($"Session {session} does not exist.");
                }
            }

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM chunks WHERE session_id = $id";
                delete.Parameters.AddWithValue("$id", session.ToString());
                delete.ExecuteNonQuery();
            }

            if (chunks.Count > 0)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO chunks (session_id, idx, start_ms, end_ms, text, dim, vector) " +
                    "VALUES ($session, $idx, $start, $end, $text, $dim, $vector)";
                insert.Parameters.AddWithValue("$session", session.ToString());
                var pIdx = insert.Parameters.Add("$idx", SqliteType.Integer);
                var pStart = insert.Parameters.Add("$start", SqliteType.Integer);
                var pEnd = insert.Parameters.Add("$end", SqliteType.Integer);
                var pText = insert.Parameters.Add("$text", SqliteType.Text);
                insert.Parameters.AddWithValue("$dim", dim);
                var pVector = insert.Parameters.Add("$vector", SqliteType.Blob);
                insert.Prepare();

                for (var i = 0; i < chunks.Count; i++)
                {
                    var blob = new byte[dim * sizeof(float)];
                    VectorSearch.ToBytes(data.AsSpan(i * dim, dim), blob);
                    pIdx.Value = i;
                    pStart.Value = infos[i].StartMs;
                    pEnd.Value = infos[i].EndMs;
                    pText.Value = infos[i].Text;
                    pVector.Value = blob;
                    insert.ExecuteNonQuery();
                }
            }

            transaction.Commit();

            // Publish only after the commit; the lock keeps loaders from observing the gap.
            _vectorCache[session] = new SessionVectors(session, dim, data, infos);
            _vectorSnapshot = null;
        }
    }

    private List<SearchHit> SearchVector(float[] query, SessionId? scope, int limit)
    {
        if (limit <= 0 || !query.AsSpan().ContainsAnyExcept(0f) || !VectorSearch.TryNormalize(query))
        {
            return [];
        }

        var sources = GetVectorSources(scope);
        return VectorSearch.Search(sources, query, limit);
    }

    /// <summary>Immutable snapshot of the cached vectors to scan; loads whatever is not cached yet.</summary>
    private SessionVectors[] GetVectorSources(SessionId? scope)
    {
        lock (_vectorLock)
        {
            if (scope is { } id)
            {
                if (!_vectorCache.TryGetValue(id, out var cached))
                {
                    if (_vectorCacheComplete)
                    {
                        return [];
                    }

                    LoadVectors(id);
                    if (!_vectorCache.TryGetValue(id, out cached))
                    {
                        // Remember "no chunks" so repeated scoped queries do not hit SQLite again.
                        cached = new SessionVectors(id, 0, [], []);
                        _vectorCache[id] = cached;
                        _vectorSnapshot = null;
                    }
                }

                return [cached];
            }

            if (!_vectorCacheComplete)
            {
                LoadVectors(null);
                _vectorCacheComplete = true;
                _vectorSnapshot = null;
            }

            return _vectorSnapshot ??= [.. _vectorCache.Values];
        }
    }

    /// <summary>Reads the chunks of one session (or of all) from SQLite into the cache. Call under <see cref="_vectorLock"/>.</summary>
    private void LoadVectors(SessionId? scope)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT session_id, start_ms, end_ms, text, dim, vector FROM chunks" +
            (scope is null ? string.Empty : " WHERE session_id = $session") + " ORDER BY session_id, idx";
        if (scope is { } only)
        {
            command.Parameters.AddWithValue("$session", only.ToString());
        }

        var infos = new List<ChunkInfo>();
        var data = new float[0];
        var current = default(SessionId);
        var dim = 0;
        byte[] buffer = [];
        var open = false;

        void Flush()
        {
            if (open)
            {
                _vectorCache[current] = new SessionVectors(current, dim, data.AsSpan(0, infos.Count * dim).ToArray(), [.. infos]);
            }

            infos.Clear();
            open = false;
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var session = SessionId.Parse(reader.GetString(0));
            var rowDim = reader.GetInt32(4);
            if (!open || session != current)
            {
                Flush();
                current = session;
                dim = rowDim;
                open = true;
                data = new float[Math.Max(dim * 16, 1)];
            }

            if (rowDim != dim || rowDim <= 0)
            {
                throw new InvalidDataException($"Chunks of session {session} have mixed dimensions.");
            }

            var length = dim * sizeof(float);
            if (buffer.Length < length)
            {
                buffer = new byte[length];
            }

            if (reader.GetBytes(5, 0, buffer, 0, length) != length)
            {
                throw new InvalidDataException($"A chunk vector of session {session} is not {dim} floats long.");
            }

            var row = infos.Count;
            if ((row + 1) * dim > data.Length)
            {
                Array.Resize(ref data, data.Length * 2);
            }

            VectorSearch.FromBytes(buffer, data.AsSpan(row * dim, dim));
            infos.Add(new ChunkInfo(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3)));
        }

        Flush();
        _vectorSnapshot = null;
    }

    /// <summary>Drops the cached vectors of a session. Call under <see cref="_vectorLock"/> after its rows are gone.</summary>
    private void InvalidateVectors(SessionId session)
    {
        if (_vectorCache.Remove(session))
        {
            _vectorSnapshot = null;
        }
    }
}
