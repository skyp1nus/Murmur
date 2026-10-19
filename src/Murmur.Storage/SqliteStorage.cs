using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Storage;

/// <summary>
/// SQLite-backed <see cref="IStorage"/>: session metadata plus one folder per session under the data root.
/// Every operation opens its own pooled connection (WAL, foreign keys on) and runs on the thread pool, so calls are
/// safe from any thread and never block the caller's thread. Transcripts live in SQLite with an FTS5 index; embedded chunks are searched by BM25 (FTS5) and by managed SIMD brute-force cosine (D-017).
/// </summary>
public sealed partial class SqliteStorage : IStorage, IDisposable
{
    private const string SessionColumns =
        "id, title, started_at, duration_ms, state, has_mic, has_system, screen_segments, error";

    private readonly StoragePaths _paths;
    private readonly Func<string?>? _recordingsFolder;
    private readonly string _connectionString;
    private readonly object _initLock = new();
    private readonly object _folderLock = new();
    private readonly ConcurrentDictionary<SessionId, string> _folders = new();
    private volatile bool _initialized;
    private volatile RecordingsFolderFallbackInfo? _lastFallback;
    private bool _disposed;

    /// <summary>Opens (and lazily creates) the storage under <paramref name="paths"/>; new sessions go to the default <c>Recordings</c> folder.</summary>
    public SqliteStorage(StoragePaths paths)
        : this(paths, null)
    {
    }

    /// <summary>
    /// Opens (and lazily creates) the storage under <paramref name="paths"/>.
    /// </summary>
    /// <param name="paths">The data root: database, settings and the default recordings folder.</param>
    /// <param name="recordingsFolder">
    /// Asked at every <see cref="CreateSessionAsync"/> for the folder that receives the new session folder (the
    /// <c>AppSettings.RecordingsDirectory</c> setting). <c>null</c> provider or a <c>null</c>/blank result = the default
    /// <c>&lt;data root&gt;/Recordings</c>. Existing sessions never move when the setting changes. An unusable value
    /// (not an absolute path, cannot be created or written) falls back to the default and is reported through
    /// <see cref="RecordingsFolderFallback"/>.
    /// </param>
    public SqliteStorage(StoragePaths paths, Func<string?>? recordingsFolder)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _recordingsFolder = recordingsFolder;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    /// <summary>Opens the storage under an explicit data root (used by tests and tools).</summary>
    public SqliteStorage(string rootDirectory)
        : this(new StoragePaths(rootDirectory))
    {
    }

    /// <summary>Opens the storage at the default location (<c>MURMUR_DATA_DIR</c> or the local app data folder).</summary>
    public static SqliteStorage CreateDefault() => new(StoragePaths.FromEnvironment());

    public string RootDirectory => _paths.Root;

    /// <summary>
    /// Raised (on the thread that creates the session, outside any lock) when the configured recordings folder was
    /// unusable and the new session was put into the default folder. The recording still starts.
    /// </summary>
    public event Action<RecordingsFolderFallbackInfo>? RecordingsFolderFallback;

    /// <summary>
    /// The fallback of the most recent <see cref="CreateSessionAsync"/>, or <c>null</c> when that session went to the
    /// configured (or default) folder as asked.
    /// </summary>
    public RecordingsFolderFallbackInfo? LastFallback => _lastFallback;

    public StoragePaths Paths => _paths;

    /// <summary>Releases the pooled connections so the database file can be moved or deleted.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_initialized)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            SqliteConnection.ClearPool(connection);
        }
    }

    private SqliteConnection OpenConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_initLock)
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_paths.Root);
            Directory.CreateDirectory(_paths.RecordingsDirectory);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode = WAL";
                wal.ExecuteNonQuery();
            }

            SchemaMigrator.Apply(connection, SchemaMigrator.Migrations);
            _initialized = true;
        }
    }

    /// <summary>Runs blocking SQLite work on the thread pool.</summary>
    private static ValueTask<T> Run<T>(Func<T> work, CancellationToken cancellationToken) =>
        new(Task.Run(work, cancellationToken));

    private static ValueTask Run(Action work, CancellationToken cancellationToken) =>
        new(Task.Run(work, cancellationToken));
}
