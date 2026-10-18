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
