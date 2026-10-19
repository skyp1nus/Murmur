using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;

namespace Murmur.Storage.Tests;

public sealed class RecordingsFolderTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 14, 9, 26, 53, TimeSpan.Zero);
    private readonly TempDataRoot _root = new();
    private readonly TempDataRoot _outside = new();
    private readonly List<SqliteStorage> _opened = [];
    private string? _configured;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var storage in _opened)
        {
            storage.Dispose();
        }

        _root.Dispose();
        _outside.Dispose();
    }

    private SqliteStorage Open(bool withProvider = true)
    {
        var paths = new StoragePaths(_root.Path);
        var storage = withProvider ? new SqliteStorage(paths, () => _configured) : new SqliteStorage(paths);
        _opened.Add(storage);
        return storage;
    }

    private void Tamper(SessionId id, string folder)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root.Path, StoragePaths.DatabaseFileName),
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET folder = $folder WHERE id = $id";
        command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$id", id.ToString());
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private string? StoredFolder(SessionId id)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root.Path, StoragePaths.DatabaseFileName),
            Pooling = false,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT folder FROM sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteScalar() as string;
    }

    [Fact]
    public async Task ConfiguredFolder_ReceivesNewSession_WithAbsolutePathInRow()
    {
        var target = Path.Combine(_outside.Path, "meetings");
        _configured = target;
        var storage = Open();

        var session = await storage.CreateSessionAsync("x", T0, Ct);

        var directory = storage.GetFiles(session.Id).Directory;
        Assert.Equal(target, Path.GetDirectoryName(directory));
        Assert.True(Directory.Exists(directory));
        Assert.Equal(directory, StoredFolder(session.Id));
        Assert.True(Path.IsPathFullyQualified(StoredFolder(session.Id)!));
        Assert.Null(storage.LastFallback);
    }

    [Fact]
    public async Task ChangingTheSetting_AppliesToTheNextSession_AndOldOnesStay()
    {
        var storage = Open();
        _configured = Path.Combine(_outside.Path, "a");
        var first = await storage.CreateSessionAsync("first", T0, Ct);
        _configured = Path.Combine(_outside.Path, "b");
        var second = await storage.CreateSessionAsync("second", T0.AddMinutes(1), Ct);
        _configured = null;
        var third = await storage.CreateSessionAsync("third", T0.AddMinutes(2), Ct);

        Assert.Equal(Path.Combine(_outside.Path, "a"), Path.GetDirectoryName(storage.GetFiles(first.Id).Directory));
        Assert.Equal(Path.Combine(_outside.Path, "b"), Path.GetDirectoryName(storage.GetFiles(second.Id).Directory));
        Assert.Equal(Path.Combine(_root.Path, "Recordings"), Path.GetDirectoryName(storage.GetFiles(third.Id).Directory));
        Assert.True(Directory.Exists(storage.GetFiles(first.Id).Directory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NullOrBlankSetting_UsesDefaultFolder_WithoutFallbackReport(string? configured)
    {
        _configured = configured;
        var storage = Open();
        var reported = new List<RecordingsFolderFallbackInfo>();
        storage.RecordingsFolderFallback += reported.Add;

        var session = await storage.CreateSessionAsync("x", T0, Ct);

        Assert.Equal(Path.Combine(_root.Path, "Recordings"), Path.GetDirectoryName(storage.GetFiles(session.Id).Directory));
        Assert.StartsWith("Recordings/", StoredFolder(session.Id), StringComparison.Ordinal);
        Assert.Null(storage.LastFallback);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task NoProvider_UsesDefaultFolder()
    {
        var storage = Open(withProvider: false);

        var session = await storage.CreateSessionAsync("x", T0, Ct);

        Assert.StartsWith("Recordings/", StoredFolder(session.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredFolderUnderTheRoot_IsStoredRelative()
    {
        _configured = Path.Combine(_root.Path, "Elsewhere", "Meetings");
        var storage = Open();

        var session = await storage.CreateSessionAsync("x", T0, Ct);

        var stored = StoredFolder(session.Id)!;
        Assert.StartsWith("Elsewhere/Meetings/", stored, StringComparison.Ordinal);
        Assert.False(Path.IsPathRooted(stored));
        Assert.Equal(Path.Combine(_root.Path, "Elsewhere", "Meetings", stored[(stored.LastIndexOf('/') + 1)..]), storage.GetFiles(session.Id).Directory);
    }

    [Fact]
    public async Task RelativeRowsFromAnOlderDatabase_StillResolve()
    {
        var storage = Open(withProvider: false);
        var session = await storage.CreateSessionAsync("old", T0, Ct);
        var name = Path.GetFileName(storage.GetFiles(session.Id).Directory);
        storage.Dispose();
        Tamper(session.Id, "Recordings/" + name);

        _configured = Path.Combine(_outside.Path, "new");
        var reopened = Open();

        Assert.Equal(Path.Combine(_root.Path, "Recordings", name), reopened.GetFiles(session.Id).Directory);
    }

    [Fact]
    public async Task FileInTheWay_FallsBackToDefault_AndReports()
    {
        var blocker = Path.Combine(_outside.Path, "not-a-folder");
        Directory.CreateDirectory(_outside.Path);
        await File.WriteAllTextAsync(blocker, "x", Ct);
        _configured = blocker;
        var storage = Open();
        var reported = new List<RecordingsFolderFallbackInfo>();
        storage.RecordingsFolderFallback += reported.Add;

        var session = await storage.CreateSessionAsync("x", T0, Ct);

        Assert.Equal(Path.Combine(_root.Path, "Recordings"), Path.GetDirectoryName(storage.GetFiles(session.Id).Directory));
        Assert.True(Directory.Exists(storage.GetFiles(session.Id).Directory));
        Assert.StartsWith("Recordings/", StoredFolder(session.Id), StringComparison.Ordinal);
        var info = Assert.Single(reported);
        Assert.Equal(session.Id, info.Session);
        Assert.Equal(blocker, info.RequestedFolder);
        Assert.False(string.IsNullOrWhiteSpace(info.Reason));
        Assert.Same(info, storage.LastFallback);
        Assert.True(File.Exists(blocker));
    }

    [Fact]
    public async Task NonRootedSetting_FallsBackToDefault_AndReports()
    {
        _configured = Path.Combine("relative", "folder");
        var storage = Open();

        var session = await storage.CreateSessionAsync("x", T0, Ct);

        Assert.Equal(Path.Combine(_root.Path, "Recordings"), Path.GetDirectoryName(storage.GetFiles(session.Id).Directory));
        Assert.Equal(_configured, storage.LastFallback?.RequestedFolder);
        Assert.False(Directory.Exists(Path.GetFullPath(_configured)));
    }

    [Fact]
    public async Task LastFallback_IsClearedByTheNextSuccessfulSession_AndFaultySubscriberIsIgnored()
    {
        _configured = "relative";
        var storage = Open();
        storage.RecordingsFolderFallback += _ => throw new InvalidOperationException("subscriber bug");

        await storage.CreateSessionAsync("a", T0, Ct);
        Assert.NotNull(storage.LastFallback);

        _configured = Path.Combine(_outside.Path, "ok");
        await storage.CreateSessionAsync("b", T0.AddMinutes(1), Ct);
        Assert.Null(storage.LastFallback);
    }

    [Fact]
    public async Task Delete_OfOutsideSession_RemovesOnlyThatFolder()
    {
        var target = Path.Combine(_outside.Path, "meetings");
        _configured = target;
        var storage = Open();
        var keep = await storage.CreateSessionAsync("keep", T0, Ct);
        var drop = await storage.CreateSessionAsync("drop", T0.AddMinutes(1), Ct);
        var bystander = Path.Combine(target, "my-notes.txt");
        await File.WriteAllTextAsync(bystander, "keep me", Ct);
        var dropFiles = storage.GetFiles(drop.Id);
        await File.WriteAllBytesAsync(dropFiles.MicrophoneAudio, [1, 2, 3], Ct);

        await storage.DeleteSessionAsync(drop.Id, Ct);

        Assert.Null(await storage.GetSessionAsync(drop.Id, Ct));
        Assert.False(Directory.Exists(dropFiles.Directory));
        Assert.True(Directory.Exists(storage.GetFiles(keep.Id).Directory));
        Assert.True(File.Exists(bystander));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task ListGetDelete_RoundTripAfterReopening()
    {
        _configured = Path.Combine(_outside.Path, "meetings");
        string directory;
        SessionId id;
        using (var first = new SqliteStorage(new StoragePaths(_root.Path), () => _configured))
        {
            var session = await first.CreateSessionAsync("persisted", T0, Ct);
            id = session.Id;
            directory = first.GetFiles(id).Directory;
        }

        _configured = null;
        var second = Open();

        Assert.Equal(id, Assert.Single(await second.ListSessionsAsync(new SessionQuery(), Ct)).Id);
        Assert.Equal("persisted", (await second.GetSessionAsync(id, Ct))!.Title);
        Assert.Equal(directory, second.GetFiles(id).Directory);

        await second.DeleteSessionAsync(id, Ct);

        Assert.False(Directory.Exists(directory));
        Assert.Empty(await second.ListSessionsAsync(new SessionQuery(), Ct));
    }

    [Fact]
    public async Task Delete_OfTamperedAbsoluteFolder_IsRefused_AndNothingIsDeleted()
    {
        var victim = Path.Combine(_outside.Path, "home");
        Directory.CreateDirectory(victim);
        var precious = Path.Combine(victim, "precious.txt");
        await File.WriteAllTextAsync(precious, "x", Ct);
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(_root.Path))!;
        var shaped = Path.Combine(victim, "2026-03-14_09-26-53_abcdef12");
        Directory.CreateDirectory(shaped);

        var tampered = new[]
        {
            victim,
            victim + Path.DirectorySeparatorChar,
            driveRoot,
            Path.GetDirectoryName(shaped)!,
            Path.Combine(victim, "2026-03-14_09-26-53_abcdef12_x"),
            Path.Combine(victim, "..", "home"),
            Path.Combine(victim, "2026-03-14_09-26-53_abcdef1"),
            Path.Combine(victim, "2026-03-14_09-26-53_abcdefgh"),
        };

        foreach (var value in tampered)
        {
            var seed = Open(withProvider: false);
            var session = await seed.CreateSessionAsync("x", T0, Ct);
            seed.Dispose();
            Tamper(session.Id, value);

            var storage = Open(withProvider: false);
            var deleteCalls = new List<string>();
            storage.DeleteFolder = deleteCalls.Add; // would-be deletions are recorded, never executed

            await Assert.ThrowsAsync<InvalidDataException>(async () => await storage.DeleteSessionAsync(session.Id, Ct));

            Assert.Empty(deleteCalls);
            Assert.NotNull(await storage.GetSessionAsync(session.Id, Ct));
            Assert.Equal(value, StoredFolder(session.Id));
        }

        Assert.True(File.Exists(precious));
        Assert.True(Directory.Exists(shaped));
    }

    [Fact]
    public async Task Delete_OfTamperedAbsoluteFolder_WithRealDeletion_LeavesTheFolderAlone()
    {
        var victim = Path.Combine(_outside.Path, "home");
        Directory.CreateDirectory(victim);
        var precious = Path.Combine(victim, "precious.txt");
        await File.WriteAllTextAsync(precious, "x", Ct);
        var seed = Open(withProvider: false);
        var session = await seed.CreateSessionAsync("x", T0, Ct);
        seed.Dispose();
        Tamper(session.Id, victim);
        var storage = Open(withProvider: false);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await storage.DeleteSessionAsync(session.Id, Ct));

        Assert.True(File.Exists(precious));
    }

    [Fact]
    public async Task Delete_OfAbsoluteFolderWithSessionShape_AndSuffix_IsAllowed()
    {
        var folder = Path.Combine(_outside.Path, "meetings", "2026-03-14_09-26-53_ABCDEF12_2");
        Directory.CreateDirectory(folder);
        var seed = Open(withProvider: false);
        var session = await seed.CreateSessionAsync("x", T0, Ct);
        seed.Dispose();
        Tamper(session.Id, folder);
        var storage = Open(withProvider: false);

        await storage.DeleteSessionAsync(session.Id, Ct);

        Assert.False(Directory.Exists(folder));
        Assert.Null(await storage.GetSessionAsync(session.Id, Ct));
    }

    [Fact]
    public async Task RelativeValueEscapingTheRoot_IsStillRefused()
    {
        var seed = Open(withProvider: false);
        var session = await seed.CreateSessionAsync("x", T0, Ct);
        seed.Dispose();
        Tamper(session.Id, "Recordings/../../escape");
        var storage = Open(withProvider: false);

        Assert.Throws<InvalidDataException>(() => storage.GetFiles(session.Id));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await storage.DeleteSessionAsync(session.Id, Ct));
        Assert.NotNull(await storage.GetSessionAsync(session.Id, Ct));
    }

    [Theory]
    [InlineData("2026-03-14_09-26-53_abcdef12", true)]
    [InlineData("2026-03-14_09-26-53_ABCDEF12", true)]
    [InlineData("2026-03-14_09-26-53_abcdef12_2", true)]
    [InlineData("2026-03-14_09-26-53_abcdef12_17", true)]
    [InlineData("2026-03-14_09-26-53_abcdef12_", false)]
    [InlineData("2026-03-14_09-26-53_abcdef12_x", false)]
    [InlineData("2026-03-14_09-26-53_abcdef123", false)]
    [InlineData("2026-03-14_09-26-53_abcdef1", false)]
    [InlineData("2026-03-14_09-26-53_abcdefgh", false)]
    [InlineData("2026-03-14 09-26-53_abcdef12", false)]
    [InlineData("Recordings", false)]
    [InlineData("", false)]
    public void IsSessionFolderName_MatchesWhatReserveFolderProduces(string name, bool expected) =>
        Assert.Equal(expected, SqliteStorage.IsSessionFolderName(name));
}
