using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Storage;

public sealed partial class SqliteStorage
{
    /// <summary>
    /// Replaces the transcript of a session (segments, words and the FTS5 index rows) in one transaction. Times are
    /// stored with millisecond precision. Throws <see cref="KeyNotFoundException"/> for an unknown session.
    /// </summary>
    public ValueTask SaveTranscriptAsync(Transcript transcript, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        return Run(() => SaveTranscript(transcript), cancellationToken);
    }

    /// <summary>Segments ordered by start time then index; <see langword="null"/> when none was saved.</summary>
    public ValueTask<Transcript?> GetTranscriptAsync(SessionId id, CancellationToken cancellationToken) =>
        Run(() => GetTranscript(id), cancellationToken);

    /// <summary>Phase 3 building block: runs an FTS5 MATCH query over segment text, best rank first.</summary>
    internal ValueTask<IReadOnlyList<SegmentMatch>> MatchSegmentsAsync(string ftsQuery, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ftsQuery);
        return Run<IReadOnlyList<SegmentMatch>>(() => MatchSegments(ftsQuery, limit), cancellationToken);
    }

    private void SaveTranscript(Transcript transcript)
    {
        var session = transcript.Session.ToString();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM sessions WHERE id = $id";
            exists.Parameters.AddWithValue("$id", session);
            if (exists.ExecuteScalar() is null)
            {
                throw new KeyNotFoundException($"Session {transcript.Session} does not exist.");
            }
        }

        using (var delete = connection.CreateCommand())
        {
            // The AFTER DELETE trigger removes the matching FTS rows.
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM segments WHERE session_id = $id";
            delete.Parameters.AddWithValue("$id", session);
            delete.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO segments (session_id, idx, speaker, start_ms, end_ms, text, words) " +
                "VALUES ($session, $idx, $speaker, $start, $end, $text, $words)";
            var pIdx = insert.Parameters.Add("$idx", SqliteType.Integer);
            var pSpeaker = insert.Parameters.Add("$speaker", SqliteType.Integer);
            var pStart = insert.Parameters.Add("$start", SqliteType.Integer);
            var pEnd = insert.Parameters.Add("$end", SqliteType.Integer);
            var pText = insert.Parameters.Add("$text", SqliteType.Text);
            var pWords = insert.Parameters.Add("$words", SqliteType.Blob);
            insert.Parameters.AddWithValue("$session", session);
            insert.Prepare();

            for (var i = 0; i < transcript.Segments.Count; i++)
            {
                var segment = transcript.Segments[i];
                pIdx.Value = i;
                pSpeaker.Value = (int)segment.Speaker;
                pStart.Value = (long)segment.Start.TotalMilliseconds;
                pEnd.Value = (long)segment.End.TotalMilliseconds;
                pText.Value = segment.Text;
                pWords.Value = WordsCodec.Encode(segment.Words);
                insert.ExecuteNonQuery();
            }
        }

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText =
                "INSERT INTO transcripts (session_id, engine, language, created_at) VALUES ($id, $engine, $language, $created) " +
                "ON CONFLICT (session_id) DO UPDATE SET engine = excluded.engine, language = excluded.language, " +
                "created_at = excluded.created_at";
            upsert.Parameters.AddWithValue("$id", session);
            upsert.Parameters.AddWithValue("$engine", transcript.Engine);
            upsert.Parameters.AddWithValue("$language", (object?)transcript.Language ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            upsert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private Transcript? GetTranscript(SessionId id)
    {
        var session = id.ToString();
        using var connection = OpenConnection();
        // One read transaction so the header and the segments come from the same snapshot.
        using var transaction = connection.BeginTransaction(deferred: true);

        string engine;
        string? language;
        using (var header = connection.CreateCommand())
        {
            header.Transaction = transaction;
            header.CommandText = "SELECT engine, language FROM transcripts WHERE session_id = $id";
            header.Parameters.AddWithValue("$id", session);
            using var reader = header.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            engine = reader.GetString(0);
            language = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var segments = new List<TranscriptSegment>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                "SELECT speaker, start_ms, end_ms, text, words FROM segments WHERE session_id = $id ORDER BY start_ms, idx";
            select.Parameters.AddWithValue("$id", session);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var words = reader.IsDBNull(4) ? [] : WordsCodec.Decode(reader.GetFieldValue<byte[]>(4));
                segments.Add(new TranscriptSegment(
                    (Speaker)reader.GetInt32(0),
                    TimeSpan.FromMilliseconds(reader.GetInt64(1)),
                    TimeSpan.FromMilliseconds(reader.GetInt64(2)),
                    reader.GetString(3),
                    words));
            }
        }

        return new Transcript(id, segments, engine, language);
    }

    private List<SegmentMatch> MatchSegments(string ftsQuery, int limit)
    {
        var matches = new List<SegmentMatch>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.session_id, s.idx, s.start_ms, s.text FROM segments_fts f JOIN segments s ON s.id = f.rowid " +
            "WHERE segments_fts MATCH $query ORDER BY f.rank LIMIT $limit";
        command.Parameters.AddWithValue("$query", ftsQuery);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            matches.Add(new SegmentMatch(
                SessionId.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                TimeSpan.FromMilliseconds(reader.GetInt64(2)),
                reader.GetString(3)));
        }

        return matches;
    }
}

/// <summary>A segment found by an FTS5 query.</summary>
internal readonly record struct SegmentMatch(SessionId Session, int Index, TimeSpan Start, string Text);
