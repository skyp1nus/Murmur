using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Services;
using Murmur.Core.Import;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>The "Import files" pill of the New recording card (Idea7Import.dc.html, "Idea 7 / Import files"): it opens the import dialog.</summary>
public sealed partial class LibraryViewModel
{
    private readonly IImportQueue? _imports;
    private readonly IMediaDecoder? _mediaProbe;
    private readonly IDownloadsFolder? _downloads;

    /// <summary>
    /// The pill is shown: the app can import files. The parity boards are built without an import queue, so this natural state keeps the
    /// pill off the Main boards.
    /// </summary>
    public bool CanImport => _imports is not null;

    /// <summary>The file dialog of the import's "browse this PC" (the view sets it to the window's storage provider).</summary>
    public IMediaFilePicker? MediaFilePicker { get; set; }

    /// <summary>The pill was pressed: the view shows this dialog over the window; the dialog closes itself (<see cref="ImportDialogViewModel.CloseRequested"/>).</summary>
    public event Action<ImportDialogViewModel>? ImportDialogRequested;

    /// <summary>The import dialog over the import queue, or <c>null</c> when the app cannot import.</summary>
    internal ImportDialogViewModel? CreateImportDialog() =>
        _imports is { } imports ? new ImportDialogViewModel(imports, _mediaProbe, _downloads, MediaFilePicker, _time, _post) : null;

    /// <summary>"Import files": opens the import dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private void OpenImport()
    {
        if (CreateImportDialog() is { } dialog)
        {
            ImportDialogRequested?.Invoke(dialog);
        }
    }

    // Imported sessions in the library (#68) ------------------------------------------------------------------------------

    /// <summary>The queue's entries by the session they made (the row of that session shows the import's state).</summary>
    private Dictionary<SessionId, ImportItem> _importBySession = [];

    /// <summary>Rows for imports that have no session yet (waiting in the queue) or failed before they made one.</summary>
    private IReadOnlyList<SessionRecord> _importRows = [];

    private HashSet<SessionId> _placeholderIds = [];

    private ImportItem[] _importSnapshot = [];

    /// <summary>The store holds an imported session, whatever the date filter says.</summary>
    private bool _importedStored;

    /// <summary>Some session of the library is imported (the Imported chip is shown).</summary>
    private bool _importedKnown;

    /// <summary>The Imported chip is on: only imported sessions are listed (combined with the date chips and the search).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImportedFilter))]
    [NotifyPropertyChangedFor(nameof(ShowImportedFilter))]
    public partial bool ImportedOnly { get; private set; }

    /// <summary>The chip's own state (a toggle, apart from the date chips).</summary>
    public bool IsImportedFilter => ImportedOnly;

    /// <summary>
    /// The Imported chip is shown: the library has at least one imported session (or the chip is on, so it can be turned off). The parity
    /// boards have none, so their natural state is the chips of the canvas.
    /// </summary>
    public bool ShowImportedFilter => ImportedOnly || _importedKnown;

    private void SubscribeImports()
    {
        if (_imports is { } imports)
        {
            SnapshotImports();
            imports.Changed += OnImportsChanged;
        }
    }

    private void UnsubscribeImports()
    {
        if (_imports is { } imports)
        {
            imports.Changed -= OnImportsChanged;
        }
    }

    /// <summary>The Imported chip was pressed.</summary>
    [RelayCommand]
    private void ToggleImported() => ImportedOnly = !ImportedOnly;

    partial void OnImportedOnlyChanged(bool value)
    {
        ApplySearch();
        if (ShowSearchResults)
        {
            StartTranscriptSearch(debounce: false);
        }
    }

    /// <summary>The queue changed (off the UI thread): when an entry came, went or changed its state, the list is read again.</summary>
    private void OnImportsChanged() => _post(() =>
    {
        if (!_disposed && SnapshotImports())
        {
            LoadTask = LoadAsync();
        }
    });

    /// <summary>Reads the queue; <c>true</c> when an entry came, went or changed its state or session (progress alone is not shown).</summary>
    private bool SnapshotImports()
    {
        var items = _imports is null ? [] : _imports.Items.ToArray();
        var changed = items.Length != _importSnapshot.Length;
        for (var i = 0; !changed && i < items.Length; i++)
        {
            var a = items[i];
            var b = _importSnapshot[i];
            changed = a.Id != b.Id || a.State != b.State || a.Session != b.Session;
        }

        if (!changed)
        {
            return false;
        }

        _importSnapshot = items;
        var bySession = new Dictionary<SessionId, ImportItem>();
        var rows = new List<SessionRecord>();
        var placeholders = new HashSet<SessionId>();
        foreach (var item in items)
        {
            if (item.Session is { } session)
            {
                bySession[session] = item;
            }
            else if (item.State is ImportState.Queued or ImportState.Decoding or ImportState.Failed)
            {
                var placeholder = PlaceholderOf(item);
                rows.Add(placeholder);
                bySession[placeholder.Id] = item;
                placeholders.Add(placeholder.Id);
            }
        }

        _importBySession = bySession;
        _importRows = rows;
        _placeholderIds = placeholders;
        return true;
    }

    /// <summary>A stand-in session for an import that has no session yet; it is dated as the queue will date the session, so it does not change its day group.</summary>
    private SessionRecord PlaceholderOf(ImportItem item) => new()
    {
        Id = new SessionId(new Guid(unchecked((int)item.Id), unchecked((short)(item.Id >> 32)), unchecked((short)(item.Id >> 48)), 0x49, 0x4d, 0x50, 0x4f, 0x52, 0x54, 0x45, 0x44)),
        Title = TitleOf(item.Path),
        StartedAt = StartedAtOf(item.Path),
        State = item.State == ImportState.Failed ? SessionState.Failed : SessionState.Recording,
        HasSystemAudio = true,
        Source = SessionSource.Imported,
        OriginalPath = item.Path,
    };

    /// <summary>What <c>ImportQueue.StartedAtOf</c> does: the file's creation time, else its last write time (unknown or implausible times fall back; a missing file reads as now).</summary>
    private DateTimeOffset StartedAtOf(string path)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        DateTime created;
        DateTime written;
        try
        {
            created = File.GetCreationTimeUtc(path);
            written = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new DateTimeOffset(now);
        }

        var floor = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var at = created > floor && created <= now.AddDays(1) ? created : written > floor ? written : now;
        return new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
    }

    /// <summary>The file name without its extension, underscores as spaces (what the queue names the session).</summary>
    private static string TitleOf(string path)
    {
        var name = SessionItemViewModel.FileNameOf(path);
        var dot = name.LastIndexOf('.');
        return (dot > 0 ? name[..dot] : name).Replace('_', ' ');
    }

    /// <summary>The queue's entries that wait for a session (shown above the stored sessions), unless a date range hides "now".</summary>
    private IEnumerable<SessionRecord> ImportPlaceholders() => Filter == LibraryFilter.Range ? [] : _importRows;

    /// <summary>Under a date filter the list cannot say whether an imported session exists: ask the store without the filter (<c>null</c> = it failed).</summary>
    private async Task<bool?> AnyImportedStoredAsync()
    {
        try
        {
            var all = await _store.ListSessionsAsync(new SessionQuery(), CancellationToken.None);
            return all.Any(r => r.Source == SessionSource.Imported);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Whether the Imported chip is shown, after a read of the list; it goes (and the filter with it) when none is left.</summary>
    private void UpdateImportedKnown()
    {
        var any = _importRows.Count > 0 || _importedStored;
        if (any == _importedKnown)
        {
            return;
        }

        _importedKnown = any;
        OnPropertyChanged(nameof(ShowImportedFilter));
        if (!any && ImportedOnly)
        {
            ImportedOnly = false;
        }
    }
}
