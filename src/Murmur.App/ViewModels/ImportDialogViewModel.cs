using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.Core.Import;

namespace Murmur.App.ViewModels;

/// <summary>
/// The import dialog (Idea7Import.dc.html, "Idea 7 / Import dialog"): the drop zone with "browse this PC" (1), the newest audio and video
/// files of the Downloads folder (2), the files to import with their kind, size and duration (3), the spoken language and what happens to
/// the original file, and "Import N files".
/// </summary>
/// <remarks>
/// <para>The Downloads folder is searched and every file is probed (<see cref="IMediaDecoder.ProbeAsync"/>, one at a time, in the order
/// added) off the UI thread; the results come back through the post callback. Only files that were probed and can be read count: an
/// unsupported or damaged file stays in the list with its reason until it is removed.</para>
/// <para>A file is in the list once (the same full path; case-insensitive on Windows). Import hands every counted file with the chosen
/// options to <see cref="IImportQueue.EnqueueAsync"/> and closes the dialog; when that throws, the dialog stays open with the error.
/// Closing (Esc, the cross, Cancel) changes nothing, and cannot happen while Import is handing the files over.</para>
/// <para>A Downloads row says Added when its file is in the list or already waits in the import queue (Queued or Decoding, from an earlier
/// dialog); the rows follow <see cref="IImportQueue.Changed"/>.</para>
/// </remarks>
public sealed partial class ImportDialogViewModel : ObservableObject, IDisposable
{
    /// <summary>How far back "Recent in Downloads" looks.</summary>
    internal static readonly TimeSpan RecentWindow = TimeSpan.FromDays(30);

    /// <summary>At most this many files in "Recent in Downloads".</summary>
    internal const int RecentLimit = 5;

    /// <summary>The order of the formats line of the mockup; other supported extensions follow it, sorted.</summary>
    private static readonly string[] FormatOrder = [".mp3", ".wav", ".m4a", ".flac", ".ogg", ".mp4", ".mkv", ".mov", ".webm"];

    /// <summary>Extensions shown with the video icon in "Recent in Downloads" (those files are not probed).</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.Ordinal) { ".mp4", ".mkv", ".mov", ".webm", ".avi", ".wmv", ".m4v" };

    private readonly IImportQueue _imports;
    private readonly IMediaDecoder? _probe;
    private readonly IDownloadsFolder? _downloads;
    private readonly IMediaFilePicker? _picker;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly StringComparer _paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly List<Task> _probes = [];
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;
    private bool _disposed;

    /// <param name="imports">Where Import hands the files; its <see cref="IImportQueue.SupportedExtensions"/> are the formats on offer.</param>
    /// <param name="probe">Reads the kind, size and duration of a file; <c>null</c> = only the size is shown and every supported file counts.</param>
    /// <param name="downloads">The Downloads folder of "Recent in Downloads"; <c>null</c> = no such section.</param>
    /// <param name="picker">The file dialog of "browse this PC"; <c>null</c> = the link does nothing.</param>
    /// <param name="time">The clock of the 30-day window and of "Last week".</param>
    /// <param name="post">Marshals a callback to the UI thread; the search and the probes finish on background threads.</param>
    public ImportDialogViewModel(
        IImportQueue imports,
        IMediaDecoder? probe,
        IDownloadsFolder? downloads,
        IMediaFilePicker? picker,
        TimeProvider time,
        Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(imports);
        ArgumentNullException.ThrowIfNull(time);
        _imports = imports;
        _probe = probe;
        _downloads = downloads;
        _picker = picker;
        _time = time;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));

        Extensions = OrderedExtensions(imports.SupportedExtensions);
        FormatsText = string.Join(Strings.Import_FormatSeparator, Extensions.Select(e => e.TrimStart('.').ToUpperInvariant()));
        var languages = new List<ImportLanguageOption>(SpokenLanguages.All.Count + 1) { new(null, Strings.Import_AutoDetect) };
        foreach (var (code, name) in SpokenLanguages.All)
        {
            languages.Add(new ImportLanguageOption(code, name));
        }

        Languages = languages;
        SelectedLanguage = languages[0];
        Files.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFiles));
        Recent.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecent));
        _imports.Changed += OnQueueChanged;
        RecentTask = LoadRecentAsync(_cts.Token);
    }

    /// <summary>Raised once by Esc, the cross, Cancel and a finished Import: the view takes the dialog away (and disposes this).</summary>
    public event Action? CloseRequested;

    /// <summary>The extensions the import accepts, in the order of the mockup's formats line.</summary>
    public IReadOnlyList<string> Extensions { get; }

    /// <summary>"MP3 · WAV · M4A · …": what the import takes on this platform, in the mockup's order.</summary>
    public string FormatsText { get; }

    /// <summary>"Recent in Downloads": the newest audio and video files of the last 30 days, at most five.</summary>
    public ObservableCollection<RecentFileViewModel> Recent { get; } = [];

    /// <summary>The section is shown: the Downloads folder is known and has recent media.</summary>
    public bool HasRecent => Recent.Count > 0;

    /// <summary>The files to import, in the order they were added.</summary>
    public ObservableCollection<ImportFileViewModel> Files { get; } = [];

    /// <summary>The list of step 3 is shown (there is at least one file, counted or not).</summary>
    public bool HasFiles => Files.Count > 0;

    /// <summary>The files that were probed and can be imported.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesTitle), nameof(ImportLabel), nameof(CanImport))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial int ValidCount { get; private set; }

    /// <summary>"2 files to import" (the counted files).</summary>
    public string FilesTitle => Plural(ValidCount, Strings.Import_CountOne, Strings.Import_CountFew, Strings.Import_CountMany);

    /// <summary>"Import 2 files".</summary>
    public string ImportLabel => Plural(ValidCount, Strings.Import_ButtonOne, Strings.Import_ButtonFew, Strings.Import_ButtonMany);

    /// <summary>Auto-detect, then the 25 languages of Parakeet in their own names.</summary>
    public IReadOnlyList<ImportLanguageOption> Languages { get; }

    [ObservableProperty]
    public partial ImportLanguageOption SelectedLanguage { get; set; }

    /// <summary>What happens to the original file; Copy to library by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCopyToLibrary), nameof(IsLeaveInPlace))]
    public partial OriginalFileMode OriginalFile { get; set; } = OriginalFileMode.CopyToLibrary;

    /// <summary>The "Copy to library" half of the segmented pair.</summary>
    public bool IsCopyToLibrary
    {
        get => OriginalFile == OriginalFileMode.CopyToLibrary;
        set
        {
            if (value)
            {
                OriginalFile = OriginalFileMode.CopyToLibrary;
            }
        }
    }

    /// <summary>The "Leave in place" half of the segmented pair.</summary>
    public bool IsLeaveInPlace
    {
        get => OriginalFile == OriginalFileMode.LeaveInPlace;
        set
        {
            if (value)
            {
                OriginalFile = OriginalFileMode.LeaveInPlace;
            }
        }
    }

    /// <summary>The files are being handed to the import (the buttons wait).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport), nameof(CanClose))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(CloseCommand))]
    public partial bool IsImporting { get; private set; }

    /// <summary>Why the import or the file dialog failed, shown in the footer in place of "Transcribed on this PC"; <c>null</c> = none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>"Import N files" can run: at least one counted file and no import running.</summary>
    public bool CanImport => ValidCount > 0 && !IsImporting;

    /// <summary>Esc, the cross and Cancel close the dialog: not while Import is handing the files over.</summary>
    public bool CanClose => !IsImporting;

    /// <summary>"browse this PC" opens a file dialog.</summary>
    public bool CanBrowse => _picker is not null;

    /// <summary>Esc, the cross, Cancel or Import closed the dialog.</summary>
    public bool IsClosed => _closed;

    /// <summary>The dialog was taken off the screen and released: files can no longer be added.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>The search of the Downloads folder (for tests).</summary>
    internal Task RecentTask { get; }

    /// <summary>Completes when every probe started so far has finished (for tests).</summary>
    internal Task WhenProbed() => Task.WhenAll([.. _probes]);

    /// <summary>Completes when <see cref="Dispose"/> has released the token source and the probe gate (for tests).</summary>
    internal Task WhenReleased => _released.Task;

    /// <summary>
    /// Adds files to the list (browse, Add, and the files dropped on the window, #69): each full path once, then probes the new ones off
    /// the UI thread. Call on the UI thread.
    /// </summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (_disposed)
        {
            return;
        }

        var added = false;
        foreach (var path in paths)
        {
            if (FullPath(path) is not { } full || Find(full) is not null)
            {
                continue;
            }

            var row = new ImportFileViewModel(full, Remove);
            Files.Add(row);
            Start(row);
            added = true;
        }

        if (added)
        {
            // An earlier failure (the file dialog, the import) is out of date once the list changed.
            Error = null;
        }

        Refresh();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _imports.Changed -= OnQueueChanged;
        _cts.Cancel();

        // The probes and the search still running hold the token and the gate: release both once they have seen the cancellation.
        var pending = Task.WhenAll([.. _probes, RecentTask]);
        if (pending.IsCompleted)
        {
            ReleaseResources(pending);
        }
        else
        {
            _ = pending.ContinueWith(ReleaseResources, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void ReleaseResources(Task finished)
    {
        _ = finished.Exception; // observed: a probe or search error was already turned into a row state (or no section)
        _cts.Dispose();
        _probeGate.Dispose();
        _released.TrySetResult();
    }

    /// <summary>"Import N files": one request per counted file with the chosen language and original-file mode, then the dialog closes.</summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        var options = new ImportOptions(SelectedLanguage.Code, OriginalFile);
        var requests = new List<ImportRequest>(ValidCount);
        foreach (var file in Files)
        {
            if (file.IsValid)
            {
                requests.Add(new ImportRequest(file.Path, options));
            }
        }

        if (requests.Count == 0)
        {
            return;
        }

        IsImporting = true;
        Error = null;
        try
        {
            // Not the dialog's token: once handed over, the files are queued even if the dialog is closed in the meantime.
            await _imports.EnqueueAsync(requests, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            IsImporting = false;
            Error = UiText.Format(Strings.Import_Failed, ex.Message);
            return;
        }

        IsImporting = false;
        Close();
    }

    /// <summary>"browse this PC": the system's file dialog with the supported formats; the chosen files join the list.</summary>
    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseAsync()
    {
        if (_picker is null)
        {
            return;
        }

        IReadOnlyList<string> picked;
        try
        {
            picked = await _picker.PickMediaFilesAsync(Strings.Import_PickerTitle, Strings.Import_FileTypeName, Extensions).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = UiText.Format(Strings.Import_PickFailed, ex.Message);
            return;
        }

        if (picked.Count > 0)
        {
            Error = null;
        }

        AddFiles(picked);
    }

    /// <summary>Esc, the cross and Cancel: the dialog goes, nothing is imported.</summary>
    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close()
    {
        // Esc and the scrim execute the command without asking CanExecute: the import being handed over keeps the dialog open as well.
        if (_closed || IsImporting)
        {
            return;
        }

        _closed = true;
        CloseRequested?.Invoke();
    }

    private void Remove(ImportFileViewModel row)
    {
        if (Files.Remove(row))
        {
            row.IsRemoved = true;
            Refresh();
        }
    }

    private void AddRecent(RecentFileViewModel recent) => AddFiles([recent.Path]);

    /// <summary>The queue changed (off the UI thread): the Added state of the Downloads rows follows it.</summary>
    private void OnQueueChanged() => _post(() =>
    {
        if (!_disposed)
        {
            Refresh();
        }
    });

    /// <summary>The file is in the list of step 3, or is in <paramref name="queued"/> (the files that wait in the import queue).</summary>
    private bool IsAdded(string fullPath, HashSet<string> queued) => Find(fullPath) is not null || queued.Contains(fullPath);

    /// <summary>One snapshot of the import queue: the full paths of the files that are Queued or Decoding.</summary>
    private HashSet<string> QueuedPaths()
    {
        var queued = new HashSet<string>(_paths);
        foreach (var item in _imports.Items)
        {
            if (item.State is (ImportState.Queued or ImportState.Decoding) && FullPath(item.Path) is { } path)
            {
                queued.Add(path);
            }
        }

        return queued;
    }

    private ImportFileViewModel? Find(string fullPath)
    {
        foreach (var file in Files)
        {
            if (_paths.Equals(file.Path, fullPath))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>The counted files, the titles and the "Added" state of the Downloads rows follow the list.</summary>
    private void Refresh()
    {
        var valid = 0;
        foreach (var file in Files)
        {
            if (file.IsValid)
            {
                valid++;
            }
        }

        ValidCount = valid;
        if (Recent.Count == 0)
        {
            return;
        }

        var queued = QueuedPaths();
        foreach (var recent in Recent)
        {
            recent.IsAdded = IsAdded(recent.Path, queued);
        }
    }

    /// <summary>An unsupported extension is answered at once; any other file is probed (or, without a probe, only its size is read).</summary>
    private void Start(ImportFileViewModel row)
    {
        if (!_imports.SupportedExtensions.Contains(CompositeMediaDecoder.ExtensionOf(row.Path)))
        {
            row.Apply(ImportFileState.Unsupported, null, null, null);
            return;
        }

        _probes.Add(ProbeAsync(row, _cts.Token));
    }

    private async Task ProbeAsync(ImportFileViewModel row, CancellationToken cancellationToken)
    {
        MediaProbe? probe = null;
        long? size = null;
        ImportFileState state;
        string? error = null;
        try
        {
            await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (row.IsRemoved)
                {
                    return;
                }

                if (_probe is { } decoder)
                {
                    probe = await Task.Run(() => decoder.ProbeAsync(row.Path, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    size = await Task.Run(() => new FileInfo(row.Path).Length, cancellationToken).ConfigureAwait(false);
                }

                state = ImportFileState.Ready;
            }
            finally
            {
                _probeGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (NotSupportedException)
        {
            state = ImportFileState.Unsupported;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            state = ImportFileState.Damaged;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            state = ImportFileState.Missing;
        }
        catch (Exception ex)
        {
            // Access denied, a file held by another program, a decoder error...: the row says why and does not count.
            state = ImportFileState.Failed;
            error = ex.Message;
        }

        _post(() =>
        {
            if (_disposed || row.IsRemoved)
            {
                return;
            }

            row.Apply(state, probe, size, error);
            Refresh();
        });
    }

    private async Task LoadRecentAsync(CancellationToken cancellationToken)
    {
        if (_downloads is not { } downloads)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var extensions = _imports.SupportedExtensions;
        IReadOnlyList<RecentMediaFile> found;
        try
        {
            found = await Task.Run(
                () => downloads.Path is { } folder ? RecentMedia.Find(folder, extensions, now, RecentWindow, RecentLimit, cancellationToken) : [],
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            // Closed before the search finished, or the folder lookup failed: no section.
            return;
        }

        if (found.Count == 0)
        {
            return;
        }

        _post(() =>
        {
            if (_disposed)
            {
                return;
            }

            var queued = QueuedPaths();
            foreach (var file in found)
            {
                var extension = CompositeMediaDecoder.ExtensionOf(file.Path);
                var detail = UiText.Format(Strings.Import_RecentDetail, Size(file.SizeBytes), Age(file.LastWriteTime, _time));
                Recent.Add(new RecentFileViewModel(file.Path, detail, VideoExtensions.Contains(extension), AddRecent) { IsAdded = IsAdded(file.Path, queued) });
            }
        });
    }

    private static string? FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>The supported extensions in the order of the mockup's formats line, then the others (sorted), lower case, each once.</summary>
    internal static IReadOnlyList<string> OrderedExtensions(IReadOnlySet<string> supported)
    {
        var lower = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in supported)
        {
            if (!string.IsNullOrWhiteSpace(extension))
            {
                lower.Add(extension.ToLowerInvariant());
            }
        }

        var ordered = new List<string>(lower.Count);
        foreach (var extension in FormatOrder)
        {
            if (lower.Remove(extension))
            {
                ordered.Add(extension);
            }
        }

        var rest = lower.ToList();
        rest.Sort(StringComparer.Ordinal);
        ordered.AddRange(rest);
        return ordered;
    }

    /// <summary>"412 MB": megabytes up to a gigabyte (the mockup's rows), then gigabytes; decimal units like the rest of the app.</summary>
    internal static string Size(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        return bytes switch
        {
            >= 1_000_000_000 => UiText.Format(Strings.Size_Gb, bytes / 1_000_000_000d),
            >= 1_000_000 => UiText.Format(Strings.Size_Mb, bytes / 1_000_000d),
            _ => UiText.Format(Strings.Size_Kb, bytes / 1_000d),
        };
    }

    /// <summary>
    /// When a file of the Downloads folder was written, in the Library's words: Today, Yesterday, This week (2 to 6 days), then Last week
    /// (7 to 13 days) and the date ("Oct 5") for older files.
    /// </summary>
    internal static string Age(DateTimeOffset written, TimeProvider time)
    {
        var local = TimeZoneInfo.ConvertTime(written, time.LocalTimeZone);
        return UiFormat.DaysAgo(local, time) switch
        {
            <= 0 => Strings.Library_FilterToday,
            1 => Strings.Library_GroupYesterday,
            <= 6 => Strings.Library_FilterWeek,
            <= 13 => Strings.Import_AgeLastWeek,
            _ => UiFormat.ShortDate(local.Date),
        };
    }

    /// <summary>The plural form of <paramref name="count"/> in the UI language: English one / other, Ukrainian one / few / many.</summary>
    internal static string Plural(int count, string one, string few, string many)
    {
        string format;
        if (string.Equals(UiCulture.Language, UiCulture.Ukrainian, StringComparison.Ordinal))
        {
            var lastDigit = count % 10;
            var lastTwo = count % 100;
            format = lastDigit == 1 && lastTwo != 11 ? one
                : lastDigit is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? few
                : many;
        }
        else
        {
            format = count == 1 ? one : many;
        }

        return UiText.Format(format, count);
    }
}

/// <summary>A spoken language of the import: its Parakeet code (<c>null</c> = auto-detect) and its name in that language.</summary>
public sealed record ImportLanguageOption(string? Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>What the import dialog knows about a file of its list.</summary>
public enum ImportFileState
{
    /// <summary>Being read ("…").</summary>
    Probing,

    /// <summary>Read: it counts in "Import N files".</summary>
    Ready,

    /// <summary>Not an audio or video format the import takes.</summary>
    Unsupported,

    /// <summary>The format is known but the file is damaged or holds no audio.</summary>
    Damaged,

    /// <summary>The file is gone.</summary>
    Missing,

    /// <summary>The file could not be opened (access denied, in use...).</summary>
    Failed,
}

/// <summary>A row of step 3: the kind icon, the name, "Video · 412 MB · 45:10" (or why it cannot be imported) and the cross that removes it.</summary>
public sealed partial class ImportFileViewModel : ObservableObject
{
    private readonly Action<ImportFileViewModel> _remove;

    internal ImportFileViewModel(string path, Action<ImportFileViewModel> remove)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path;
        _remove = remove;
        Detail = Strings.Import_Probing;
        RemoveLabel = UiText.Format(Strings.Import_Remove, Name);
    }

    /// <summary>The full path.</summary>
    public string Path { get; }

    /// <summary>The file name.</summary>
    public string Name { get; }

    /// <summary>The accessible name of the cross: "Remove notes.pdf".</summary>
    public string RemoveLabel { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(IsProbing), nameof(IsDimmed), nameof(IsAudio), nameof(IsVideo))]
    public partial ImportFileState State { get; private set; } = ImportFileState.Probing;

    /// <summary>Audio or video once probed; <c>null</c> before, or when the probe could not tell.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAudio), nameof(IsVideo))]
    public partial MediaKind? Kind { get; private set; }

    /// <summary>"Video · 412 MB · 45:10", "…" while probing, or the reason it cannot be imported.</summary>
    [ObservableProperty]
    public partial string Detail { get; private set; }

    /// <summary>Counts in "Import N files".</summary>
    public bool IsValid => State == ImportFileState.Ready;

    public bool IsProbing => State == ImportFileState.Probing;

    /// <summary>The row is faded (the file cannot be imported).</summary>
    public bool IsDimmed => State is not (ImportFileState.Ready or ImportFileState.Probing);

    /// <summary>The waveform icon.</summary>
    public bool IsAudio => IsValid && Kind == MediaKind.Audio;

    /// <summary>The camera icon (any other row has the file icon).</summary>
    public bool IsVideo => IsValid && Kind == MediaKind.Video;

    /// <summary>The row left the list; a probe still running for it is dropped.</summary>
    internal bool IsRemoved { get; set; }

    [RelayCommand]
    private void Remove() => _remove(this);

    internal void Apply(ImportFileState state, MediaProbe? probe, long? size, string? error)
    {
        Kind = probe?.Kind;
        Detail = state switch
        {
            ImportFileState.Ready when probe is not null => UiText.Format(
                Strings.Import_FileDetail,
                probe.Kind == MediaKind.Video ? Strings.Import_KindVideo : Strings.Import_KindAudio,
                ImportDialogViewModel.Size(probe.SizeBytes),
                UiFormat.Clock(probe.Duration)),
            ImportFileState.Ready => ImportDialogViewModel.Size(size ?? 0),
            ImportFileState.Unsupported => Strings.Import_Unsupported,
            ImportFileState.Damaged => Strings.Import_Damaged,
            ImportFileState.Missing => Strings.Import_Missing,
            ImportFileState.Failed => UiText.Format(Strings.Import_ReadFailed, error),
            _ => Strings.Import_Probing,
        };
        State = state;
    }
}

/// <summary>
/// A row of "Recent in Downloads": the kind icon, the name, "58 MB · Last week" and Add (Added once the file is in the list or in the import
/// queue).
/// </summary>
public sealed partial class RecentFileViewModel : ObservableObject
{
    private readonly Action<RecentFileViewModel> _add;

    internal RecentFileViewModel(string path, string detail, bool isVideo, Action<RecentFileViewModel> add)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path;
        Detail = detail;
        IsVideo = isVideo;
        _add = add;
        AddAccessibleName = UiText.Format(Strings.Import_AddFile, Name);
    }

    public string Path { get; }

    public string Name { get; }

    /// <summary>"58 MB · Last week".</summary>
    public string Detail { get; }

    /// <summary>The camera icon (by extension: these rows are not probed); the waveform otherwise.</summary>
    public bool IsVideo { get; }

    /// <summary>The accessible name of the pill: "Add podcast-ep42.mp3".</summary>
    public string AddAccessibleName { get; }

    /// <summary>The file is in the list of step 3 or waits in the import queue: the pill says Added and is disabled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddLabel))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool IsAdded { get; set; }

    /// <summary>"Add", or "Added" once the file is in the list.</summary>
    public string AddLabel => IsAdded ? Strings.Import_Added : Strings.Import_Add;

    private bool CanAdd() => !IsAdded;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add() => _add(this);
}
