using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;

namespace Murmur.Storage;

public sealed partial class SqliteStorage
{
    /// <summary>
    /// Replaces the chapters of the session (an empty list removes them) in one transaction. Starts are stored with millisecond precision.
    /// A session that does not exist throws <see cref="KeyNotFoundException"/>.
    /// </summary>
    public ValueTask SaveChaptersAsync(SessionId session, IReadOnlyList<Chapter> chapters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        return Run(() => SaveChapters(session, chapters), cancellationToken);
    }

    /// <summary>The chapters of the session ordered by start; empty when there are none. (Deleting the session removes them through the cascade.)</summary>
    public ValueTask<IReadOnlyList<Chapter>> GetChaptersAsync(SessionId session, CancellationToken cancellationToken) =>
        Run(() => GetChapters(session), cancellationToken);

    private IReadOnlyList<Chapter> GetChapters(SessionId session)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title, start_ms FROM chapters WHERE session_id = $id ORDER BY start_ms, idx";
        command.Parameters.AddWithValue("$id", session.ToString());
        using var reader = command.ExecuteReader();
        var chapters = new List<Chapter>();
        while (reader.Read())
        {
            chapters.Add(new Chapter(reader.GetString(0), TimeSpan.FromMilliseconds(reader.GetInt64(1))));
        }

        return chapters;
    }

    private void SaveChapters(SessionId session, IReadOnlyList<Chapter> chapters)
    {
        using var connection = OpenConnection();

        // The check and the write are one transaction (like SaveSummary): a session deleted in between is a KeyNotFoundException.
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
            delete.CommandText = "DELETE FROM chapters WHERE session_id = $id";
            delete.Parameters.AddWithValue("$id", session.ToString());
            delete.ExecuteNonQuery();
        }

        if (chapters.Count > 0)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO chapters (session_id, idx, title, start_ms) VALUES ($id, $idx, $title, $start)";
            var pIdx = insert.Parameters.Add("$idx", SqliteType.Integer);
            var pTitle = insert.Parameters.Add("$title", SqliteType.Text);
            var pStart = insert.Parameters.Add("$start", SqliteType.Integer);
            insert.Parameters.AddWithValue("$id", session.ToString());
            for (var i = 0; i < chapters.Count; i++)
            {
                pIdx.Value = i;
                pTitle.Value = chapters[i].Title;
                pStart.Value = (long)chapters[i].Start.TotalMilliseconds;
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }
}
