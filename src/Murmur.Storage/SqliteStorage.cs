using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Storage;

/// <summary>
/// SQLite-backed <see cref="IStorage"/>: session metadata plus one folder per session under the data root.
/// Every operation opens its own pooled connection (WAL, foreign keys on) and runs on the thread pool, so calls are
/// safe from any thread and never block the caller's thread. Transcript and search members arrive in later phases.
/// </summary>
public sealed partial class SqliteStorage : IStorage, IDisposable
{
    private const string SessionColumns =
        "id, title, started_at, duration_ms, state, has_mic, has_system, screen_segments, error";

    private readonly StoragePaths _paths;
    private readonly string _connectionString;
    private readonly object _initLock = new();
    private readonly object _folderLock = new();
    private readonly ConcurrentDictionary<SessionId, string> _folders = new();
    private volatile bool _initialized;
    private bool _disposed;

    /// <summary>Opens (and lazily creates) the storage under <paramref name="paths"/>.</summary>
    public SqliteStorage(StoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
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
