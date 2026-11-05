using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;

namespace Murmur.Storage;

public sealed partial class SqliteStorage
{
    /// <summary>
    /// Adds the bookmarks in one transaction and returns every bookmark of the session ordered by position. Positions are stored with
    /// millisecond precision. A session that does not exist throws <see cref="KeyNotFoundException"/> (nothing is stored).
    /// </summary>
    public ValueTask<IReadOnlyList<Bookmark>> AddBookmarksAsync(SessionId session, IReadOnlyList<NewBookmark> bookmarks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);
        foreach (var bookmark in bookmarks)
        {
            ArgumentNullException.ThrowIfNull(bookmark);
            ArgumentNullException.ThrowIfNull(bookmark.Note);
        }

        return Run(() => AddBookmarks(session, bookmarks), cancellationToken);
    }

    /// <summary>Ordered by position, then by id.</summary>
    public ValueTask<IReadOnlyList<Bookmark>> GetBookmarksAsync(SessionId session, CancellationToken cancellationToken) =>
        Run<IReadOnlyList<Bookmark>>(
            () =>
            {
                using var connection = OpenConnection();
                return ReadBookmarks(connection, null, session);
            },
            cancellationToken);

    /// <summary>An unknown id throws <see cref="KeyNotFoundException"/>.</summary>
    public ValueTask UpdateBookmarkNoteAsync(long id, string note, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(note);
        return Run(
            () =>
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE bookmarks SET note = $note WHERE id = $id";
                command.Parameters.AddWithValue("$note", note);
                command.Parameters.AddWithValue("$id", id);
                if (command.ExecuteNonQuery() == 0)
                {
                    throw new KeyNotFoundException($"Bookmark {id} does not exist.");
                }
            },
            cancellationToken);
    }

    /// <summary>An unknown id is ignored.</summary>
    public ValueTask DeleteBookmarkAsync(long id, CancellationToken cancellationToken) =>
        Run(
            () =>
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM bookmarks WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            },
            cancellationToken);

    private IReadOnlyList<Bookmark> AddBookmarks(SessionId session, IReadOnlyList<NewBookmark> bookmarks)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!SessionExists(connection, transaction, session))
        {
            throw new KeyNotFoundException($"Session {session} does not exist.");
        }

        if (bookmarks.Count > 0)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO bookmarks (session_id, at_ms, note) VALUES ($session, $at, $note)";
            var sessionParameter = insert.Parameters.Add("$session", SqliteType.Text);
            var atParameter = insert.Parameters.Add("$at", SqliteType.Integer);
            var noteParameter = insert.Parameters.Add("$note", SqliteType.Text);
            sessionParameter.Value = session.ToString();
            foreach (var bookmark in bookmarks)
            {
                atParameter.Value = (long)bookmark.At.TotalMilliseconds;
                noteParameter.Value = bookmark.Note;
                insert.ExecuteNonQuery();
            }
        }

        var all = ReadBookmarks(connection, transaction, session);
        transaction.Commit();
        return all;
    }

    private static bool SessionExists(SqliteConnection connection, SqliteTransaction transaction, SessionId id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteScalar() is not null;
    }

    private static List<Bookmark> ReadBookmarks(SqliteConnection connection, SqliteTransaction? transaction, SessionId session)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, at_ms, note FROM bookmarks WHERE session_id = $session ORDER BY at_ms, id";
        command.Parameters.AddWithValue("$session", session.ToString());
        var result = new List<Bookmark>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Bookmark(reader.GetInt64(0), session, TimeSpan.FromMilliseconds(reader.GetInt64(1)), reader.GetString(2)));
        }

        return result;
    }
}
