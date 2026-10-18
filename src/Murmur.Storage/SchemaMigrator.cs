using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Murmur.Storage;

/// <summary>
/// Versioned schema: <c>PRAGMA user_version</c> holds the number of applied migrations, and
/// <see cref="Migrations"/> is the ordered list. Append only, never edit a shipped entry.
/// </summary>
internal static class SchemaMigrator
{
    public static readonly string[] Migrations =
    [
        // 1: sessions
        """
        CREATE TABLE sessions (
            id              TEXT    PRIMARY KEY NOT NULL,
            title           TEXT    NOT NULL,
            started_at      INTEGER NOT NULL,
            duration_ms     INTEGER NOT NULL,
            state           INTEGER NOT NULL,
            has_mic         INTEGER NOT NULL,
            has_system      INTEGER NOT NULL,
            screen_segments TEXT    NOT NULL,
            folder          TEXT    NOT NULL,
            error           TEXT
        );
        CREATE INDEX ix_sessions_started_at ON sessions (started_at DESC);
        """,

        // 2: transcripts, segments and the FTS5 index over segment text. The FTS table is external-content; the
        // triggers keep it in sync for every insert/update/delete, including rows removed by ON DELETE CASCADE.
        """
        CREATE TABLE transcripts (
            session_id TEXT    PRIMARY KEY NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
            engine     TEXT    NOT NULL,
            language   TEXT,
            created_at INTEGER NOT NULL
        );
        CREATE TABLE segments (
            id         INTEGER PRIMARY KEY,
            session_id TEXT    NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
            idx        INTEGER NOT NULL,
            speaker    INTEGER NOT NULL,
            start_ms   INTEGER NOT NULL,
            end_ms     INTEGER NOT NULL,
            text       TEXT    NOT NULL,
            words      BLOB
        );
        CREATE INDEX ix_segments_session_start ON segments (session_id, start_ms);
        CREATE VIRTUAL TABLE segments_fts USING fts5 (
            text,
            content = 'segments',
            content_rowid = 'id',
            tokenize = 'unicode61 remove_diacritics 2'
        );
        CREATE TRIGGER segments_fts_ai AFTER INSERT ON segments BEGIN
            INSERT INTO segments_fts (rowid, text) VALUES (new.id, new.text);
        END;
        CREATE TRIGGER segments_fts_ad AFTER DELETE ON segments BEGIN
            INSERT INTO segments_fts (segments_fts, rowid, text) VALUES ('delete', old.id, old.text);
        END;
        CREATE TRIGGER segments_fts_au AFTER UPDATE OF text ON segments BEGIN
            INSERT INTO segments_fts (segments_fts, rowid, text) VALUES ('delete', old.id, old.text);
            INSERT INTO segments_fts (rowid, text) VALUES (new.id, new.text);
        END;
        """,
    ];

    public static int LatestVersion => Migrations.Length;

    public static int GetVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Applies the migrations the database has not seen yet, atomically. Returns the resulting version.</summary>
    public static int Apply(SqliteConnection connection, IReadOnlyList<string> migrations)
    {
        Execute(connection, "BEGIN IMMEDIATE");
        try
        {
            var current = GetVersion(connection);
            if (current > migrations.Count)
            {
                throw new InvalidOperationException(
                    $"The database schema version {current} is newer than this build supports ({migrations.Count}).");
            }

            for (var i = current; i < migrations.Count; i++)
            {
                Execute(connection, migrations[i]);
            }

            if (current != migrations.Count)
            {
                Execute(connection, "PRAGMA user_version = " + migrations.Count.ToString(CultureInfo.InvariantCulture));
            }

            Execute(connection, "COMMIT");
            return migrations.Count;
        }
        catch
        {
            Execute(connection, "ROLLBACK", ignoreErrors: true);
            throw;
        }
    }

    private static void Execute(SqliteConnection connection, string sql, bool ignoreErrors = false)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        catch (SqliteException) when (ignoreErrors)
        {
        }
    }
}
