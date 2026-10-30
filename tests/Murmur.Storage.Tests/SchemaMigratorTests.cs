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

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void ADatabaseFromBeforeTheChats_GainsTheChatTables_KeepsItsSessions_AndTheSummariesOfVersionFour(int from)
    {
        using var connection = InMemory();
        SchemaMigrator.Apply(connection, [.. SchemaMigrator.Migrations.Take(from)]);
        Assert.Equal(from, SchemaMigrator.GetVersion(connection));
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO sessions (id, title, started_at, duration_ms, state, has_mic, has_system, screen_segments, folder) " +
                "VALUES ('a', 'kept', 0, 0, 1, 0, 0, '[]', 'f')";
            insert.ExecuteNonQuery();
        }

        if (from >= 4)
        {
            using var summary = connection.CreateCommand();
            summary.CommandText = "INSERT INTO summaries (session_id, overview, key_points, action_items, model_id, created_at) VALUES ('a', 'o', '[]', '[]', 'm', 0)";
            summary.ExecuteNonQuery();
        }

        var version = SchemaMigrator.Apply(connection, SchemaMigrator.Migrations);

        Assert.Equal(5, version);
        Assert.Equal(SchemaMigrator.LatestVersion, version);
        using var count = connection.CreateCommand();
        count.CommandText =
            "SELECT (SELECT count(*) FROM sessions), (SELECT count(*) FROM sqlite_master WHERE name IN ('chat_threads', 'chat_messages', 'summaries')), " +
            "(SELECT count(*) FROM summaries)";
        using var reader = count.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(3L, reader.GetInt64(1));
        Assert.Equal(from >= 4 ? 1L : 0L, reader.GetInt64(2));
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
