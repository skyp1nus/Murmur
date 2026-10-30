using Microsoft.Data.Sqlite;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.Storage.Tests;

public sealed class SummaryStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 10, 30, 0, TimeSpan.Zero);
    private readonly TempDataRoot _root = new();
    private readonly List<SqliteStorage> _opened = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var storage in _opened)
        {
            storage.Dispose();
        }

        _root.Dispose();
    }

    private SqliteStorage Open()
    {
        var storage = _root.Open();
        _opened.Add(storage);
        return storage;
    }

    private static SessionSummary Summary(SessionId id, string overview = "Walked the client through the roadmap.") => new(
        id,
        overview,
        [new SummaryItem("PDF export — next sprint", TimeSpan.FromMilliseconds(772_250)), new SummaryItem("No moment cited", null)],
        [new SummaryItem("Send the \"demo\" invite", new TimeSpan(1, 2, 5))],
        "gemma-4-E4B-it-Q4_K_M",
        new DateTimeOffset(2026, 10, 18, 12, 0, 0, 123, TimeSpan.Zero));

    private static async Task<SessionId> NewSession(SqliteStorage storage) =>
        (await storage.CreateSessionAsync("t", T0, Ct)).Id;

    [Fact]
    public async Task ASessionWithoutASummary_HasNone()
    {
        var storage = Open();
        var id = await NewSession(storage);

        Assert.Null(await storage.GetSummaryAsync(id, Ct));
    }

    [Fact]
    public async Task ASavedSummary_ComesBackWithEveryField()
    {
        var storage = Open();
        var id = await NewSession(storage);
        var saved = Summary(id);

        await storage.SaveSummaryAsync(saved, Ct);
        var loaded = await storage.GetSummaryAsync(id, Ct);

        Assert.NotNull(loaded);
        Assert.Equal(saved.Session, loaded.Session);
        Assert.Equal(saved.Overview, loaded.Overview);
        Assert.Equal(saved.ModelId, loaded.ModelId);
        Assert.Equal(saved.CreatedAt, loaded.CreatedAt);
        Assert.Equal(saved.KeyPoints, loaded.KeyPoints);
        Assert.Equal(saved.ActionItems, loaded.ActionItems);
        Assert.Null(loaded.KeyPoints[1].At);
    }

    [Fact]
    public async Task ASummary_SurvivesReopeningTheDatabase()
    {
        var id = SessionId.New();
        using (var first = _root.Open())
        {
            id = (await first.CreateSessionAsync("t", T0, Ct)).Id;
            await first.SaveSummaryAsync(Summary(id), Ct);
        }

        var loaded = await Open().GetSummaryAsync(id, Ct);

        Assert.Equal("Walked the client through the roadmap.", loaded?.Overview);
    }

    [Fact]
    public async Task SavingAgain_ReplacesTheEarlierSummary()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.SaveSummaryAsync(Summary(id), Ct);

        await storage.SaveSummaryAsync(Summary(id, "Second version") with { KeyPoints = [], ActionItems = [] }, Ct);

        var loaded = await storage.GetSummaryAsync(id, Ct);
        Assert.Equal("Second version", loaded?.Overview);
        Assert.Empty(loaded!.KeyPoints);
        Assert.Empty(loaded.ActionItems);
        Assert.Equal(1L, Scalar(storage, "SELECT count(*) FROM summaries"));
    }

    [Fact]
    public async Task SummariesOfDifferentSessions_AreKeptApart()
    {
        var storage = Open();
        var first = await NewSession(storage);
        var second = await NewSession(storage);
        await storage.SaveSummaryAsync(Summary(first, "one"), Ct);
        await storage.SaveSummaryAsync(Summary(second, "two"), Ct);

        Assert.Equal("one", (await storage.GetSummaryAsync(first, Ct))?.Overview);
        Assert.Equal("two", (await storage.GetSummaryAsync(second, Ct))?.Overview);
    }

    [Fact]
    public async Task SavingForAnUnknownSession_Throws()
    {
        var storage = Open();

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await storage.SaveSummaryAsync(Summary(SessionId.New()), Ct));
    }

    [Fact]
    public async Task DeleteSummary_RemovesOnlyThatSummary_AndIsHarmlessWhenThereIsNone()
    {
        var storage = Open();
        var id = await NewSession(storage);
        var other = await NewSession(storage);
        await storage.SaveSummaryAsync(Summary(id), Ct);
        await storage.SaveSummaryAsync(Summary(other), Ct);

        await storage.DeleteSummaryAsync(id, Ct);
        await storage.DeleteSummaryAsync(id, Ct);

        Assert.Null(await storage.GetSummaryAsync(id, Ct));
        Assert.NotNull(await storage.GetSummaryAsync(other, Ct));
        Assert.NotNull(await storage.GetSessionAsync(id, Ct));
    }

    [Fact]
    public async Task DeletingTheSession_DeletesItsSummary()
    {
        var storage = Open();
        var id = await NewSession(storage);
        var other = await NewSession(storage);
        await storage.SaveSummaryAsync(Summary(id), Ct);
        await storage.SaveSummaryAsync(Summary(other), Ct);

        await storage.DeleteSessionAsync(id, Ct);

        Assert.Null(await storage.GetSummaryAsync(id, Ct));
        Assert.NotNull(await storage.GetSummaryAsync(other, Ct));
        Assert.Equal(1L, Scalar(storage, "SELECT count(*) FROM summaries"));
    }

    [Fact]
    public async Task ADatabaseFromBeforeSummaries_IsMigratedAndKeepsItsSessions()
    {
        // A database at schema version 3 (what the previous release created) with one session in it.
        Directory.CreateDirectory(_root.Path);
        var databasePath = new StoragePaths(_root.Path).DatabasePath;
        var id = SessionId.New();
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            old.Open();
            SchemaMigrator.Apply(old, [.. SchemaMigrator.Migrations.Take(3)]);
            using var insert = old.CreateCommand();
            insert.CommandText =
                "INSERT INTO sessions (id, title, started_at, duration_ms, state, has_mic, has_system, screen_segments, folder, error) " +
                "VALUES ($id, 'old', 0, 0, 6, 0, 0, '[]', 'x', NULL)";
            insert.Parameters.AddWithValue("$id", id.ToString());
            insert.ExecuteNonQuery();
            Assert.Equal(3, SchemaMigrator.GetVersion(old));
        }

        var storage = Open();

        Assert.Equal("old", (await storage.GetSessionAsync(id, Ct))?.Title);
        Assert.Null(await storage.GetSummaryAsync(id, Ct));
        await storage.SaveSummaryAsync(Summary(id), Ct);
        Assert.NotNull(await storage.GetSummaryAsync(id, Ct));
        Assert.Equal((long)SchemaMigrator.LatestVersion, Scalar(storage, "PRAGMA user_version"));
    }

    /// <summary>Runs a scalar query on a fresh connection to the database of <paramref name="storage"/>.</summary>
    private static long Scalar(SqliteStorage storage, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = storage.Paths.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
