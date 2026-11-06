using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Storage.Tests;

public sealed class SpeakerNameStoreTests : IDisposable
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

    private static async Task<SessionId> NewSession(SqliteStorage storage) =>
        (await storage.CreateSessionAsync("t", T0, Ct)).Id;

    [Fact]
    public async Task ASessionWithoutNames_HasTheDefaults()
    {
        var storage = Open();
        var id = await NewSession(storage);

        var names = await storage.GetSpeakerNamesAsync(id, Ct);

        Assert.Equal(SpeakerNames.Default, names);
        Assert.Null(names[Speaker.Me]);
        Assert.Null(names[Speaker.Others]);
    }

    [Fact]
    public async Task AnUnknownSession_HasTheDefaults_WhenRead()
    {
        Assert.Equal(SpeakerNames.Default, await Open().GetSpeakerNamesAsync(SessionId.New(), Ct));
    }

    [Fact]
    public async Task ANameRoundTrips_PerSpeaker_WithoutTouchingTheOther()
    {
        var storage = Open();
        var id = await NewSession(storage);

        await storage.SetSpeakerNameAsync(id, Speaker.Others, "Anna K.", Ct);
        Assert.Equal(new SpeakerNames(null, "Anna K."), await storage.GetSpeakerNamesAsync(id, Ct));

        await storage.SetSpeakerNameAsync(id, Speaker.Me, "Олена", Ct);
        var names = await storage.GetSpeakerNamesAsync(id, Ct);
        Assert.Equal(new SpeakerNames("Олена", "Anna K."), names);
        Assert.Equal("Олена", names[Speaker.Me]);
        Assert.Equal("Anna K.", names[Speaker.Others]);
    }

    [Fact]
    public async Task Names_SurviveReopeningTheDatabase()
    {
        SessionId id;
        using (var first = _root.Open())
        {
            id = (await first.CreateSessionAsync("t", T0, Ct)).Id;
            await first.SetSpeakerNameAsync(id, Speaker.Me, "Олена", Ct);
        }

        Assert.Equal(new SpeakerNames("Олена", null), await Open().GetSpeakerNamesAsync(id, Ct));
    }

    [Fact]
    public async Task SettingAgain_ReplacesTheName()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.SetSpeakerNameAsync(id, Speaker.Me, "One", Ct);

        await storage.SetSpeakerNameAsync(id, Speaker.Me, "Two", Ct);

        Assert.Equal(new SpeakerNames("Two", null), await storage.GetSpeakerNamesAsync(id, Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public async Task AnEmptyName_GoesBackToTheDefault_AndKeepsTheOtherSpeaker(string? empty)
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.SetSpeakerNameAsync(id, Speaker.Me, "Олена", Ct);
        await storage.SetSpeakerNameAsync(id, Speaker.Others, "Anna", Ct);

        await storage.SetSpeakerNameAsync(id, Speaker.Me, empty, Ct);

        Assert.Equal(new SpeakerNames(null, "Anna"), await storage.GetSpeakerNamesAsync(id, Ct));
    }

    [Fact]
    public async Task ResettingBothNames_LeavesNoRowBehind()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.SetSpeakerNameAsync(id, Speaker.Me, "a", Ct);
        await storage.SetSpeakerNameAsync(id, Speaker.Others, "b", Ct);

        await storage.SetSpeakerNameAsync(id, Speaker.Me, "", Ct);
        await storage.SetSpeakerNameAsync(id, Speaker.Others, null, Ct);

        Assert.Equal(SpeakerNames.Default, await storage.GetSpeakerNamesAsync(id, Ct));
        Assert.Equal(0L, Scalar(storage, "SELECT count(*) FROM speaker_names"));
    }

    [Fact]
    public async Task ANameIsTrimmed_AndLineBreaksBecomeSpaces()
    {
        var storage = Open();
        var id = await NewSession(storage);

        await storage.SetSpeakerNameAsync(id, Speaker.Me, "  Anna\r\nK.\n ", Ct);

        Assert.Equal("Anna  K.", (await storage.GetSpeakerNamesAsync(id, Ct)).Me);
    }

    [Fact]
    public async Task ALongName_IsCutTo40Characters()
    {
        var storage = Open();
        var id = await NewSession(storage);

        await storage.SetSpeakerNameAsync(id, Speaker.Others, new string('я', 65), Ct);

        Assert.Equal(new string('я', 40), (await storage.GetSpeakerNamesAsync(id, Ct)).Others);
        Assert.Equal(40, SqliteStorage.MaxSpeakerNameLength);
    }

    [Fact]
    public async Task ACutName_DoesNotEndOnHalfOfASurrogatePair()
    {
        var storage = Open();
        var id = await NewSession(storage);

        // 39 letters, then an emoji (two UTF-16 units) that would straddle the limit.
        await storage.SetSpeakerNameAsync(id, Speaker.Me, new string('a', 39) + "😀" + "tail", Ct);

        var name = (await storage.GetSpeakerNamesAsync(id, Ct)).Me!;
        Assert.Equal(new string('a', 39), name);
    }

    [Fact]
    public async Task SettingForAnUnknownSession_Throws_AndStoresNothing()
    {
        var storage = Open();

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await storage.SetSpeakerNameAsync(SessionId.New(), Speaker.Me, "x", Ct));
        Assert.Equal(0L, Scalar(storage, "SELECT count(*) FROM speaker_names"));
    }

    [Fact]
    public async Task NamesOfDifferentSessions_AreKeptApart()
    {
        var storage = Open();
        var first = await NewSession(storage);
        var second = await NewSession(storage);
        await storage.SetSpeakerNameAsync(first, Speaker.Me, "one", Ct);
        await storage.SetSpeakerNameAsync(second, Speaker.Me, "two", Ct);

        Assert.Equal("one", (await storage.GetSpeakerNamesAsync(first, Ct)).Me);
        Assert.Equal("two", (await storage.GetSpeakerNamesAsync(second, Ct)).Me);
    }

    [Fact]
    public async Task DeletingTheSession_DeletesItsNames_AndOnlyThose()
    {
        var storage = Open();
        var id = await NewSession(storage);
        var other = await NewSession(storage);
        await storage.SetSpeakerNameAsync(id, Speaker.Me, "gone", Ct);
        await storage.SetSpeakerNameAsync(other, Speaker.Me, "kept", Ct);

        await storage.DeleteSessionAsync(id, Ct);

        Assert.Equal(SpeakerNames.Default, await storage.GetSpeakerNamesAsync(id, Ct));
        Assert.Equal("kept", (await storage.GetSpeakerNamesAsync(other, Ct)).Me);
        Assert.Equal(1L, Scalar(storage, "SELECT count(*) FROM speaker_names"));
    }

    [Fact]
    public async Task ANewTranscript_KeepsTheNames()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.SetSpeakerNameAsync(id, Speaker.Others, "Anna", Ct);

        await storage.SaveTranscriptAsync(new Transcript(id, [], "engine"), Ct);

        Assert.Equal("Anna", (await storage.GetSpeakerNamesAsync(id, Ct)).Others);
    }

    [Fact]
    public void TheNamesTable_IsMigrationEight_AndADatabaseOfVersionSevenGainsIt()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        SchemaMigrator.Apply(connection, [.. SchemaMigrator.Migrations.Take(7)]);

        var version = SchemaMigrator.Apply(connection, SchemaMigrator.Migrations);

        Assert.Equal(SchemaMigrator.LatestVersion, version);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'speaker_names'";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    private static long Scalar(SqliteStorage storage, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = storage.Paths.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
