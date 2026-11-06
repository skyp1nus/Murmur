namespace Murmur.Core.Import;

/// <summary>One persisted entry of the import queue: its current state and what was asked for.</summary>
public sealed record StoredImport(ImportItem Item, ImportRequest Request);

/// <summary>
/// Persistence of the import queue, so unfinished imports restart after an app restart. An entry goes with its session:
/// deleting the session deletes the entry.
/// </summary>
public interface IImportStore
{
    /// <summary>
    /// Inserts the entry when <see cref="ImportItem.Id"/> is 0 and returns it with its new id (always positive);
    /// otherwise updates that entry and returns it unchanged. An entry that no longer exists (deleted with its session)
    /// is not re-created.
    /// </summary>
    ValueTask<ImportItem> SaveImportAsync(StoredImport import, CancellationToken cancellationToken);

    /// <summary>Every stored entry, oldest first.</summary>
    ValueTask<IReadOnlyList<StoredImport>> LoadImportsAsync(CancellationToken cancellationToken);

    /// <summary>An unknown id is a no-op.</summary>
    ValueTask DeleteImportAsync(long id, CancellationToken cancellationToken);
}
