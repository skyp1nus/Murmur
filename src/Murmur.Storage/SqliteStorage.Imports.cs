using Microsoft.Data.Sqlite;
using Murmur.Core.Import;
using Murmur.Core.Sessions;

namespace Murmur.Storage;

public sealed partial class SqliteStorage
{
    /// <summary>
    /// Id 0 inserts a row and returns the item with its new id; any other id updates that row (state, progress, session,
    /// error; the request never changes) and returns the item as given. A row that is gone (its session was deleted) is not
    /// re-created. A session that does not exist throws <see cref="SqliteException"/> (foreign key).
    /// </summary>
    public ValueTask<ImportItem> SaveImportAsync(StoredImport import, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(import.Item);
        ArgumentNullException.ThrowIfNull(import.Request);
        ArgumentNullException.ThrowIfNull(import.Request.Options);
        return Run(() => SaveImport(import), cancellationToken);
    }

    /// <summary>Every row, oldest (lowest id) first.</summary>
    public ValueTask<IReadOnlyList<StoredImport>> LoadImportsAsync(CancellationToken cancellationToken) =>
        Run(LoadImports, cancellationToken);

    /// <summary>An unknown id is ignored.</summary>
    public ValueTask DeleteImportAsync(long id, CancellationToken cancellationToken) =>
        Run(
            () =>
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM imports WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            },
            cancellationToken);

    private ImportItem SaveImport(StoredImport import)
    {
        var item = import.Item;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (item.Id == 0)
        {
            command.CommandText =
                "INSERT INTO imports (path, language, original_mode, state, progress, session_id, error) " +
                "VALUES ($path, $language, $mode, $state, $progress, $session, $error) RETURNING id";
            command.Parameters.AddWithValue("$path", import.Request.Path);
            command.Parameters.AddWithValue("$language", (object?)import.Request.Options.Language ?? DBNull.Value);
            command.Parameters.AddWithValue("$mode", (int)import.Request.Options.OriginalFile);
        }
        else
        {
            command.CommandText =
                "UPDATE imports SET state = $state, progress = $progress, session_id = $session, error = $error WHERE id = $id";
            command.Parameters.AddWithValue("$id", item.Id);
        }

        command.Parameters.AddWithValue("$state", (int)item.State);
        command.Parameters.AddWithValue("$progress", item.Progress);
        command.Parameters.AddWithValue("$session", item.Session is { } session ? session.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)item.Error ?? DBNull.Value);
        if (item.Id != 0)
        {
            command.ExecuteNonQuery();
            return item;
        }

        var id = (long)command.ExecuteScalar()!;
        return item with { Id = id };
    }

    private IReadOnlyList<StoredImport> LoadImports()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, path, language, original_mode, state, progress, session_id, error FROM imports ORDER BY id";
        var result = new List<StoredImport>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(1);
            var options = new ImportOptions(reader.IsDBNull(2) ? null : reader.GetString(2), (OriginalFileMode)reader.GetInt32(3));
            var item = new ImportItem(
                reader.GetInt64(0),
                path,
                (ImportState)reader.GetInt32(4),
                reader.GetDouble(5),
                reader.IsDBNull(6) ? null : SessionId.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7));
            result.Add(new StoredImport(item, new ImportRequest(path, options)));
        }

        return result;
    }
}
