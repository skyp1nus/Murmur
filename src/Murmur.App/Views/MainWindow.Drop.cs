using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Murmur.App.Services;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

/// <summary>
/// Files dragged onto the window (#69): the content area (not the sidebar) takes the platform's file drag and drop (Avalonia's
/// <see cref="DragDrop"/>); the overlay shows while they are over it and a drop opens the import dialog with them.
/// </summary>
public partial class MainWindow
{
    private ImportDialog? _importDialog;
    private bool _dropWired;
    private ShellViewModel? _shell;

    // The dragged paths, read once when the drag enters and kept until it leaves or drops (every mouse move would otherwise rebuild them).
    private List<string>? _dragPaths;
    private bool _dragParsed;
    private bool _leavePending;

    /// <summary>The import dialog a drop opened and that is still on screen (for tests), or <c>null</c>.</summary>
    internal ImportDialog? DropImportDialog => _importDialog is { IsOpen: true } ? _importDialog : null;

    private void WireDrop()
    {
        if (_dropWired)
        {
            return;
        }

        _dropWired = true;
        // The window takes the events (not only the content area): the import dialog sits on the overlay layer above the content and a drop
        // on its scrim must still reach us; the position decides whether the sidebar or the content area is meant.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnShellChanged(ShellViewModel? shell)
    {
        if (ReferenceEquals(_shell, shell))
        {
            return;
        }

        if (_shell is not null)
        {
            _shell.ImportDialogRequested -= OnImportDialogRequested;
        }

        _shell = shell;
        if (shell is not null)
        {
            // The dialog of a drop is the Library's dialog: it gets the file dialog of this window.
            shell.Library.MediaFilePicker ??= new TopLevelMediaFilePicker(() => this);
            shell.ImportDialogRequested += OnImportDialogRequested;
        }
    }

    private void OnImportDialogRequested(ImportDialogViewModel dialog)
    {
        _importDialog?.Close();
        _importDialog = ImportDialog.Show(ContentArea, dialog);
        if (_importDialog is null)
        {
            dialog.Dispose();
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        // Moving from one element to another raises a leave and, in the same step, an enter: the files stay read then.
        if (!_leavePending)
        {
            _dragParsed = false;
            _dragPaths = null;
        }

        OnDragOver(sender, e);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        _leavePending = false;
        if (DataContext is not ShellViewModel { DropOverlay: { } overlay })
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (!_dragParsed)
        {
            _dragPaths = PathsOf(e);
            _dragParsed = true;
        }

        e.DragEffects = HandleDragOver(overlay, _dragPaths, e.GetPosition(ContentArea)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Avalonia raises a leave for the element the pointer left, also when it moves to another element of the window, and the enter of the
    /// next one follows at once. So the overlay is hidden only when no enter, over or drop has come by the time the queued step runs.
    /// </summary>
    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _leavePending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_leavePending)
                {
                    return;
                }

                _leavePending = false;
                EndDrag();
                _shell?.DropOverlay?.DragLeave();
            },
            DispatcherPriority.Normal);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = _dragParsed ? _dragPaths : PathsOf(e);
        _leavePending = false;
        EndDrag();
        if (DataContext is not ShellViewModel { DropOverlay: { } overlay })
        {
            return;
        }

        HandleDrop(overlay, files, e.GetPosition(ContentArea));
        e.Handled = true;
    }

    private void EndDrag()
    {
        _dragPaths = null;
        _dragParsed = false;
    }

    /// <summary>A drag moved over the window at <paramref name="position"/> (in the content area's coordinates); true = a drop would be taken.</summary>
    internal bool HandleDragOver(DropOverlayViewModel overlay, IReadOnlyList<string>? files, Point position)
    {
        if (!InContent(position))
        {
            overlay.DragLeave();
            return false;
        }

        return overlay.DragOver(files);
    }

    /// <summary>Files were dropped at <paramref name="position"/>; true = taken.</summary>
    internal bool HandleDrop(DropOverlayViewModel overlay, IReadOnlyList<string>? files, Point position)
    {
        if (!InContent(position))
        {
            overlay.DragLeave();
            return false;
        }

        return overlay.Drop(files);
    }

    private bool InContent(Point position) =>
        position.X >= 0 && position.Y >= 0 && position.X <= ContentArea.Bounds.Width && position.Y <= ContentArea.Bounds.Height;

    /// <summary>The local paths of the dragged files (folders and items without a local path left out); <c>null</c> when the data holds no files.</summary>
    private static List<string>? PathsOf(DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            return null;
        }

        var paths = new List<string>();
        if (e.DataTransfer.TryGetFiles() is { } items)
        {
            foreach (var item in items)
            {
                if (item is IStorageFile && item.TryGetLocalPath() is { Length: > 0 } path)
                {
                    paths.Add(path);
                }
            }
        }

        return paths;
    }
}
