using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.App.Resources;

namespace Murmur.App.ViewModels;

/// <summary>
/// The overlay that shows while files are dragged over the window (Idea7Import.dc.html, "Idea 7 / Drag anywhere"): a dashed outline of the
/// content area with "Drop to import N files" and "They’ll be transcribed one by one". The window's drag handlers feed it the dragged paths
/// (<c>null</c> = the data holds no files, so nothing shows); a drop hands the files to <see cref="FilesDropped"/>.
/// </summary>
/// <remarks>
/// The overlay counts the files whose extension the import supports; when there are only unsupported ones it says so, and while a recording
/// runs it says that imports wait until it stops and the drop is ignored. Nothing is shown unless a drag is over the window, so the design
/// parity boards never see it.
/// </remarks>
public sealed partial class DropOverlayViewModel : ObservableObject
{
    private readonly HashSet<string> _supported;
    private readonly Func<bool> _recordingActive;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _dialogOpen;

    // What the last DragOver showed: the same dragged list in the same state is not formatted again on every mouse move.
    private IReadOnlyList<string>? _lastFiles;
    private bool _lastRecording;
    private bool _lastDialog;

    /// <param name="supportedExtensions">The extensions the import accepts, lower case with the dot (<c>IImportQueue.SupportedExtensions</c>).</param>
    /// <param name="recordingActive">A recording is running or starting/stopping: imports wait and drops are ignored.</param>
    /// <param name="enabled">Dropping is possible on the page on screen (not during the onboarding); <c>null</c> = always.</param>
    /// <param name="dialogOpen">The import dialog is open: its scrim covers the page, so the overlay stays hidden (the drop is still taken); <c>null</c> = never.</param>
    public DropOverlayViewModel(IReadOnlySet<string> supportedExtensions, Func<bool> recordingActive, Func<bool>? enabled = null, Func<bool>? dialogOpen = null)
    {
        ArgumentNullException.ThrowIfNull(supportedExtensions);
        ArgumentNullException.ThrowIfNull(recordingActive);
        _supported = new HashSet<string>(supportedExtensions, StringComparer.OrdinalIgnoreCase);
        _dialogOpen = dialogOpen ?? (static () => false);
        _recordingActive = recordingActive;
        _enabled = enabled ?? (static () => true);
    }

    /// <summary>The overlay is on screen (it fades in and out).</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; private set; }

    /// <summary>The bold line.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    /// <summary>The Smoke line under it.</summary>
    [ObservableProperty]
    public partial string Hint { get; private set; } = string.Empty;

    /// <summary>The drag is over the window and a drop would be taken (supported files, no recording).</summary>
    public bool CanDrop { get; private set; }

    /// <summary>Raised on a drop that is taken: the dropped paths, in the order given (unsupported ones too: the import dialog marks them).</summary>
    public event Action<IReadOnlyList<string>>? FilesDropped;

    /// <summary>
    /// Files are dragged over the content area. <paramref name="files"/> are the local paths of the dragged files, <c>null</c> when the data
    /// is no files at all. Returns whether a drop would be accepted (the drag cursor: copy, or none).
    /// </summary>
    public bool DragOver(IReadOnlyList<string>? files)
    {
        if (files is null || !_enabled())
        {
            Hide();
            return false;
        }

        var recording = _recordingActive();
        var dialog = _dialogOpen();
        if (ReferenceEquals(files, _lastFiles) && recording == _lastRecording && dialog == _lastDialog)
        {
            return CanDrop;
        }

        _lastFiles = files;
        _lastRecording = recording;
        _lastDialog = dialog;
        if (recording)
        {
            Show(Strings.DropOverlay_Recording, Strings.DropOverlay_RecordingHint, canDrop: false);
            return false;
        }

        var supported = Count(files);
        if (supported == 0)
        {
            Show(Strings.DropOverlay_Unsupported, Strings.DropOverlay_UnsupportedHint, canDrop: false);
            return false;
        }

        Show(
            ImportDialogViewModel.Plural(supported, Strings.DropOverlay_TitleOne, Strings.DropOverlay_TitleFew, Strings.DropOverlay_TitleMany),
            Strings.DropOverlay_Hint,
            canDrop: true);
        return true;
    }

    /// <summary>The drag left the content area, or was cancelled.</summary>
    public void DragLeave() => Hide();

    /// <summary>
    /// The files were dropped. The overlay goes; the files are handed on unless a recording runs, the page takes no drops, or there are no
    /// files. Returns whether they were taken.
    /// </summary>
    public bool Drop(IReadOnlyList<string>? files)
    {
        Hide();
        if (files is not { Count: > 0 } || !_enabled() || _recordingActive())
        {
            return false;
        }

        FilesDropped?.Invoke(files);
        return true;
    }

    private int Count(IReadOnlyList<string> files)
    {
        var count = 0;
        foreach (var file in files)
        {
            if (_supported.Contains(Path.GetExtension(file)))
            {
                count++;
            }
        }

        return count;
    }

    private void Show(string title, string hint, bool canDrop)
    {
        Title = title;
        Hint = hint;
        CanDrop = canDrop;
        IsVisible = !_dialogOpen();
    }

    private void Hide()
    {
        _lastFiles = null;
        CanDrop = false;
        IsVisible = false;
    }
}
