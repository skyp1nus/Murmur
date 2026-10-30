using Microsoft.Data.Sqlite;

namespace Murmur.Storage.Tests;

public sealed class SchemaMigratorTests
{
    private static SqliteConnection InMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    [Fact]
    public void EmptyDatabase_IsMigratedToLatest()
    {
        using var connection = InMemory();
        Assert.Equal(0, SchemaMigrator.GetVersion(connection));

        var version = SchemaMigrator.Apply(connection, SchemaMigrator.Migrations);

        Assert.Equal(SchemaMigrator.LatestVersion, version);
        Assert.Equal(SchemaMigrator.LatestVersion, SchemaMigrator.GetVersion(connection));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'sessions'";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void DatabaseFromBeforeTheChats_GainsTheChatTables_AndKeepsItsSessions()
    {
        using var connection = InMemory();
        SchemaMigrator.Apply(connection, [.. SchemaMigrator.Migrations.Take(3)]);
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO sessions (id, title, started_at, duration_ms, state, has_mic, has_system, screen_segments, folder) " +
                "VALUES ('a', 'kept', 0, 0, 1, 0, 0, '[]', 'f')";
            insert.ExecuteNonQuery();
        }

        var version = SchemaMigrator.Apply(connection, SchemaMigrator.Migrations);

        Assert.Equal(SchemaMigrator.LatestVersion, version);
        Assert.True(version >= 4);
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT (SELECT count(*) FROM sessions), (SELECT count(*) FROM sqlite_master WHERE name IN ('chat_threads', 'chat_messages'))";
        using var reader = count.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(2L, reader.GetInt64(1));
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        using var connection = InMemory();

        SchemaMigrator.Apply(connection, SchemaMigrator.Migrations);
        SchemaMigrator.Apply(connection, SchemaMigrator.Migrations);

        Assert.Equal(SchemaMigrator.LatestVersion, SchemaMigrator.GetVersion(connection));
    }

    [Fact]
    public void OnlyMissingMigrationsRun_AndDataIsKept()
    {
        using var connection = InMemory();
        string[] v1 = ["CREATE TABLE a (x INTEGER);"];
        string[] v2 = [.. v1, "ALTER TABLE a ADD COLUMN y TEXT;"];
        SchemaMigrator.Apply(connection, v1);
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO a (x) VALUES (7)";
            insert.ExecuteNonQuery();
        }

        SchemaMigrator.Apply(connection, v2);

        Assert.Equal(2, SchemaMigrator.GetVersion(connection));
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT x, y FROM a";
        using var reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(7L, reader.GetInt64(0));
        Assert.True(reader.IsDBNull(1));
    }

    [Fact]
    public void FailingMigration_RollsBackEverything()
    {
        using var connection = InMemory();
        string[] broken = ["CREATE TABLE ok (x INTEGER);", "THIS IS NOT SQL;"];

        Assert.Throws<SqliteException>(() => SchemaMigrator.Apply(connection, broken));

        Assert.Equal(0, SchemaMigrator.GetVersion(connection));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'ok'";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void NewerDatabase_IsRejected()
    {
        using var connection = InMemory();
        SchemaMigrator.Apply(connection, ["CREATE TABLE a (x INTEGER);", "CREATE TABLE b (x INTEGER);"]);

        Assert.Throws<InvalidOperationException>(() => SchemaMigrator.Apply(connection, ["CREATE TABLE a (x INTEGER);"]));
    }
}
