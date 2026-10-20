using System.Globalization;
using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Storage.Tests;

public sealed class SqliteStorageTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 14, 9, 26, 53, TimeSpan.Zero);
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

    [Fact]
    public async Task Create_ReturnsRecordingSession_AndCreatesFolder()
    {
        var storage = Open();

        var session = await storage.CreateSessionAsync("Standup", T0, Ct);

        Assert.Equal("Standup", session.Title);
        Assert.Equal(SessionState.Recording, session.State);
        Assert.Equal(T0, session.StartedAt);
        Assert.True(Directory.Exists(storage.GetFiles(session.Id).Directory));
        Assert.True(File.Exists(Path.Combine(_root.Path, "murmur.db")));
    }

    [Fact]
    public async Task Create_FolderNameFollowsLocalTimeAndIdPrefix()
    {
        var storage = Open();

        var session = await storage.CreateSessionAsync("x", T0, Ct);

        var expected = T0.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)
            + "_" + session.Id.ToString()[..8];
        Assert.Equal(Path.Combine(_root.Path, "Recordings", expected), storage.GetFiles(session.Id).Directory);
    }

    [Fact]
    public async Task Create_SameSecondTwice_GetsDistinctFolders()
    {
        var storage = Open();

        var a = await storage.CreateSessionAsync("a", T0, Ct);
        var b = await storage.CreateSessionAsync("b", T0, Ct);

        Assert.NotEqual(storage.GetFiles(a.Id).Directory, storage.GetFiles(b.Id).Directory);
        Assert.True(Directory.Exists(storage.GetFiles(a.Id).Directory));
        Assert.True(Directory.Exists(storage.GetFiles(b.Id).Directory));
    }

    [Fact]
    public async Task Get_UnknownSession_ReturnsNull()
    {
        var storage = Open();

        Assert.Null(await storage.GetSessionAsync(SessionId.New(), Ct));
    }

    [Fact]
    public async Task Update_RoundTripsEveryField()
    {
        var storage = Open();
        var created = await storage.CreateSessionAsync("Draft", T0, Ct);
        var updated = created with
        {
            Duration = TimeSpan.FromMilliseconds(3_723_456),
            State = SessionState.Failed,
            HasMicrophone = true,
            HasSystemAudio = true,
            ScreenSegments =
            [
                new ScreenSegment(0, TimeSpan.Zero, TimeSpan.FromSeconds(30)),
                new ScreenSegment(1, TimeSpan.FromSeconds(45.5), TimeSpan.FromMilliseconds(1234)),
            ],
            Error = "boom \"quoted\" ünïcode",
        };

        await storage.UpdateSessionAsync(updated, Ct);
        var read = await storage.GetSessionAsync(created.Id, Ct);

        Assert.NotNull(read);
        Assert.Equal(updated.Id, read.Id);
        Assert.Equal("Draft", read.Title); // the title is changed by RenameSessionAsync only
        Assert.Equal(updated.StartedAt, read.StartedAt);
        Assert.Equal(updated.Duration, read.Duration);
        Assert.Equal(updated.State, read.State);
        Assert.True(read.HasMicrophone);
        Assert.True(read.HasSystemAudio);
        Assert.Equal(updated.ScreenSegments, read.ScreenSegments);
        Assert.Equal(updated.Error, read.Error);
    }

    [Fact]
    public async Task Update_NeverChangesTheTitle_SoAnOldCopyOfTheRecordCannotPutAnOldTitleBack()
    {
        var storage = Open();
        var created = await storage.CreateSessionAsync("Draft", T0, Ct);
        await storage.RenameSessionAsync(created.Id, "Chosen by the user", Ct);

        await storage.UpdateSessionAsync(created with { Title = "Draft", State = SessionState.Recorded, Duration = TimeSpan.FromSeconds(9) }, Ct);

        var read = await storage.GetSessionAsync(created.Id, Ct);
        Assert.Equal("Chosen by the user", read!.Title);
        Assert.Equal(SessionState.Recorded, read.State);
        Assert.Equal(TimeSpan.FromSeconds(9), read.Duration);
    }

    [Fact]
    public async Task Rename_ChangesTheTitleOnly()
    {
        var storage = Open();
        var created = await storage.CreateSessionAsync("Draft", T0, Ct);
        var full = created with
        {
            Duration = TimeSpan.FromSeconds(5),
            State = SessionState.Recorded,
            HasMicrophone = true,
            Error = "rec.mic.denied",
            ScreenSegments = [new ScreenSegment(0, TimeSpan.Zero, TimeSpan.FromSeconds(3))],
        };
        await storage.UpdateSessionAsync(full, Ct);

        await storage.RenameSessionAsync(created.Id, "Új név — “quoted”", Ct);

        var read = await storage.GetSessionAsync(created.Id, Ct);
        Assert.Equal("Új név — “quoted”", read!.Title);
        Assert.Equal(full.StartedAt, read.StartedAt);
        Assert.Equal(full.Duration, read.Duration);
        Assert.Equal(full.State, read.State);
        Assert.True(read.HasMicrophone);
        Assert.Equal(full.Error, read.Error);
        Assert.Equal(full.ScreenSegments, read.ScreenSegments);
    }

    [Fact]
    public async Task Rename_UnknownSession_Throws()
    {
        var storage = Open();

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await storage.RenameSessionAsync(SessionId.New(), "x", Ct));
    }

    [Fact]
    public async Task Rename_RacingWithUpdates_NeverRollsBackTheRestOfTheRow()
    {
        var storage = Open();
        var created = await storage.CreateSessionAsync("Draft", T0, Ct);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            if (i % 2 == 0)
            {
                await storage.RenameSessionAsync(created.Id, "title " + i, Ct);
            }
            else
            {
                await storage.UpdateSessionAsync(created with { State = SessionState.Recorded, Duration = TimeSpan.FromSeconds(i) }, Ct);
            }
        }, Ct)));

        var read = await storage.GetSessionAsync(created.Id, Ct);
        Assert.StartsWith("title ", read!.Title, StringComparison.Ordinal);
        Assert.Equal(SessionState.Recorded, read.State);
    }

    [Fact]
    public async Task Update_ClearingError_StoresNull()
    {
        var storage = Open();
        var created = await storage.CreateSessionAsync("x", T0, Ct);
        await storage.UpdateSessionAsync(created with { Error = "bad", State = SessionState.Failed }, Ct);

        await storage.UpdateSessionAsync(created with { Error = null, State = SessionState.Ready }, Ct);

        var read = await storage.GetSessionAsync(created.Id, Ct);
        Assert.Null(read!.Error);
        Assert.Equal(SessionState.Ready, read.State);
    }

    [Fact]
    public async Task Update_UnknownSession_Throws()
    {
        var storage = Open();
        var ghost = new SessionRecord { Id = SessionId.New(), Title = "ghost", StartedAt = T0 };

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await storage.UpdateSessionAsync(ghost, Ct));
    }

    [Fact]
    public async Task List_IsNewestFirst()
    {
        var storage = Open();
        var oldest = await storage.CreateSessionAsync("oldest", T0, Ct);
        var newest = await storage.CreateSessionAsync("newest", T0.AddHours(2), Ct);
        var middle = await storage.CreateSessionAsync("middle", T0.AddHours(1), Ct);

        var list = await storage.ListSessionsAsync(new SessionQuery(), Ct);

        Assert.Equal([newest.Id, middle.Id, oldest.Id], list.Select(s => s.Id));
    }

    [Fact]
    public async Task List_FiltersByRange_InclusiveBounds()
    {
        var storage = Open();
        var a = await storage.CreateSessionAsync("a", T0, Ct);
        var b = await storage.CreateSessionAsync("b", T0.AddDays(1), Ct);
        var c = await storage.CreateSessionAsync("c", T0.AddDays(2), Ct);

        var middle = await storage.ListSessionsAsync(new SessionQuery(From: T0.AddDays(1), To: T0.AddDays(1)), Ct);
        var fromOnly = await storage.ListSessionsAsync(new SessionQuery(From: T0.AddDays(1)), Ct);
        var toOnly = await storage.ListSessionsAsync(new SessionQuery(To: T0.AddDays(1)), Ct);

        Assert.Equal([b.Id], middle.Select(s => s.Id));
        Assert.Equal([c.Id, b.Id], fromOnly.Select(s => s.Id));
        Assert.Equal([b.Id, a.Id], toOnly.Select(s => s.Id));
    }

    [Fact]
    public async Task List_RespectsLimit()
    {
        var storage = Open();
        for (var i = 0; i < 5; i++)
        {
            await storage.CreateSessionAsync("s" + i, T0.AddMinutes(i), Ct);
        }

        var two = await storage.ListSessionsAsync(new SessionQuery(Limit: 2), Ct);
        var none = await storage.ListSessionsAsync(new SessionQuery(Limit: 0), Ct);

        Assert.Equal(["s4", "s3"], two.Select(s => s.Title));
        Assert.Empty(none);
    }

    [Fact]
    public async Task Delete_RemovesRowAndFolderRecursively()
    {
        var storage = Open();
        var session = await storage.CreateSessionAsync("x", T0, Ct);
        var files = storage.GetFiles(session.Id);
        await File.WriteAllBytesAsync(files.MicrophoneAudio, [1, 2, 3], Ct);
        Directory.CreateDirectory(Path.Combine(files.Directory, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(files.Directory, "nested", "a.bin"), [1], Ct);

        await storage.DeleteSessionAsync(session.Id, Ct);

        Assert.Null(await storage.GetSessionAsync(session.Id, Ct));
        Assert.False(Directory.Exists(files.Directory));
        Assert.Empty(await storage.ListSessionsAsync(new SessionQuery(), Ct));
        Assert.Throws<KeyNotFoundException>(() => storage.GetFiles(session.Id));
    }

    [Fact]
    public async Task Delete_OnlyAffectsThatSession()
    {
        var storage = Open();
        var keep = await storage.CreateSessionAsync("keep", T0, Ct);
        var drop = await storage.CreateSessionAsync("drop", T0.AddMinutes(1), Ct);

        await storage.DeleteSessionAsync(drop.Id, Ct);

        Assert.NotNull(await storage.GetSessionAsync(keep.Id, Ct));
        Assert.True(Directory.Exists(storage.GetFiles(keep.Id).Directory));
    }

    [Fact]
    public async Task Delete_UnknownSession_IsNoOp()
    {
        var storage = Open();

        await storage.DeleteSessionAsync(SessionId.New(), Ct);
    }

    [Fact]
    public async Task Delete_WhenFolderCannotBeRemoved_KeepsRowAndAllowsRetry()
    {
        var storage = Open();
        var session = await storage.CreateSessionAsync("x", T0, Ct);
        var files = storage.GetFiles(session.Id);
        await File.WriteAllBytesAsync(files.MicrophoneAudio, [1, 2, 3], Ct);
        storage.DeleteFolder = _ => throw new IOException("file is in use");

        await Assert.ThrowsAsync<IOException>(async () => await storage.DeleteSessionAsync(session.Id, Ct));

        Assert.NotNull(await storage.GetSessionAsync(session.Id, Ct));
        Assert.True(File.Exists(files.MicrophoneAudio));
        Assert.Equal(files.Directory, storage.GetFiles(session.Id).Directory);

        storage.DeleteFolder = static folder => Directory.Delete(folder, recursive: true);
        await storage.DeleteSessionAsync(session.Id, Ct);

        Assert.Null(await storage.GetSessionAsync(session.Id, Ct));
        Assert.False(Directory.Exists(files.Directory));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public async Task Delete_WhenFileHeldOpen_KeepsRow_UnverifiedOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Open files block directory deletion only on Windows.");
        var storage = Open();
        var session = await storage.CreateSessionAsync("x", T0, Ct);
        var files = storage.GetFiles(session.Id);
        var stream = new FileStream(files.MicrophoneAudio, FileMode.Create, FileAccess.Write, FileShare.None);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(async () => await storage.DeleteSessionAsync(session.Id, Ct));
            Assert.NotNull(await storage.GetSessionAsync(session.Id, Ct));
        }
        finally
        {
            await stream.DisposeAsync();
        }

        await storage.DeleteSessionAsync(session.Id, Ct);
        Assert.Null(await storage.GetSessionAsync(session.Id, Ct));
    }

    [Fact]
    public async Task Delete_WhenFolderAlreadyGone_StillRemovesRow()
    {
        var storage = Open();
        var session = await storage.CreateSessionAsync("x", T0, Ct);
        Directory.Delete(storage.GetFiles(session.Id).Directory, recursive: true);

        await storage.DeleteSessionAsync(session.Id, Ct);

        Assert.Null(await storage.GetSessionAsync(session.Id, Ct));
    }

    [Fact]
    public async Task Data_SurvivesReopen()
    {
        SessionRecord created;
        using (var first = _root.Open())
        {
            created = await first.CreateSessionAsync("persisted", T0, Ct);
            await first.UpdateSessionAsync(
                created with { State = SessionState.Ready, HasMicrophone = true, Duration = TimeSpan.FromMinutes(5) },
                Ct);
        }

        var second = Open();
        var read = await second.GetSessionAsync(created.Id, Ct);

        Assert.NotNull(read);
        Assert.Equal("persisted", read.Title);
        Assert.Equal(SessionState.Ready, read.State);
        Assert.True(read.HasMicrophone);
        Assert.Equal(TimeSpan.FromMinutes(5), read.Duration);
        Assert.Single(await second.ListSessionsAsync(new SessionQuery(), Ct));
    }

    [Fact]
    public async Task GetFiles_WorksForSessionCreatedByPreviousInstance()
    {
        SessionRecord created;
        string expectedDirectory;
        using (var first = _root.Open())
        {
            created = await first.CreateSessionAsync("old run", T0, Ct);
            expectedDirectory = first.GetFiles(created.Id).Directory;
        }

        var second = Open();
        var files = second.GetFiles(created.Id);

        Assert.Equal(expectedDirectory, files.Directory);
        Assert.True(Directory.Exists(files.Directory));
        Assert.Equal(Path.Combine(expectedDirectory, "mic.wav"), files.MicrophoneAudio);
    }

    [Fact]
    public void GetFiles_UnknownSession_Throws()
    {
        var storage = Open();

        Assert.Throws<KeyNotFoundException>(() => storage.GetFiles(SessionId.New()));
    }

    [Fact]
    public async Task Delete_WorksForSessionCreatedByPreviousInstance()
    {
        SessionRecord created;
        using (var first = _root.Open())
        {
            created = await first.CreateSessionAsync("old run", T0, Ct);
        }

        var second = Open();
        var directory = second.GetFiles(created.Id).Directory;
        await second.DeleteSessionAsync(created.Id, Ct);

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task ConcurrentCreates_AllSucceedWithDistinctIdsAndFolders()
    {
        var storage = Open();

        var sessions = await Task.WhenAll(Enumerable.Range(0, 64).Select(i =>
            Task.Run(async () => await storage.CreateSessionAsync("c" + i, T0, Ct), Ct)));

        Assert.Equal(64, sessions.Select(s => s.Id).Distinct().Count());
        var folders = sessions.Select(s => storage.GetFiles(s.Id).Directory).ToArray();
        Assert.Equal(64, folders.Distinct().Count());
        Assert.All(folders, f => Assert.True(Directory.Exists(f)));
        Assert.Equal(64, (await storage.ListSessionsAsync(new SessionQuery(Limit: 1000), Ct)).Count);
    }

    [Fact]
    public async Task ConcurrentMixedOperations_DoNotFail()
    {
        var storage = Open();
        var seed = await storage.CreateSessionAsync("seed", T0, Ct);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            await storage.UpdateSessionAsync(seed with { Title = "t" + i, Duration = TimeSpan.FromSeconds(i) }, Ct);
            await storage.GetSessionAsync(seed.Id, Ct);
            await storage.ListSessionsAsync(new SessionQuery(), Ct);
        }, Ct)));

        Assert.NotNull(await storage.GetSessionAsync(seed.Id, Ct));
    }

    [Fact]
    public async Task ConcurrentFirstUse_InitialisesSchemaOnce()
    {
        var storage = Open();

        var lists = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(async () => await storage.ListSessionsAsync(new SessionQuery(), Ct), Ct)));

        Assert.All(lists, l => Assert.Empty(l));
    }

    [Fact]
    public async Task EmptyDatabase_IsMigratedAndConfigured()
    {
        var storage = Open();
        await storage.ListSessionsAsync(new SessionQuery(), Ct);

        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root.Path, "murmur.db")};Pooling=False");
        connection.Open();
        Assert.Equal(SchemaMigrator.LatestVersion, SchemaMigrator.GetVersion(connection));
        using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", mode.ExecuteScalar());
        Assert.True(Directory.Exists(Path.Combine(_root.Path, "Recordings")));
    }

    [Fact]
    public async Task ForeignKeys_AreEnabledOnOperationConnections()
    {
        var storage = Open();
        await storage.ListSessionsAsync(new SessionQuery(), Ct);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root.Path, "murmur.db"),
            ForeignKeys = true,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void RootDirectory_IsTheDataRoot()
    {
        var storage = Open();

        Assert.Equal(Path.GetFullPath(_root.Path), storage.RootDirectory);
    }

    [Fact]
    public async Task CancelledToken_CancelsOperation()
    {
        var storage = Open();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await storage.CreateSessionAsync("x", T0, cts.Token));
    }

    [Fact]
    public async Task UsedAfterDispose_Throws()
    {
        var storage = _root.Open();
        await storage.ListSessionsAsync(new SessionQuery(), Ct);
        storage.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await storage.ListSessionsAsync(new SessionQuery(), Ct));
    }
}
