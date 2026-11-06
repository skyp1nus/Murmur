using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
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
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnShellChanged(ShellViewModel? shell)
    {
        if (shell is null)
        {
            return;
        }

        shell.MediaFilePicker = new TopLevelMediaFilePicker(() => this);
        shell.ImportDialogRequested += OnImportDialogRequested;
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

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not ShellViewModel { DropOverlay: { } overlay })
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = HandleDragOver(overlay, PathsOf(e), e.GetPosition(ContentArea)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => (DataContext as ShellViewModel)?.DropOverlay?.DragLeave();

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ShellViewModel { DropOverlay: { } overlay })
        {
            return;
        }

        HandleDrop(overlay, PathsOf(e), e.GetPosition(ContentArea));
        e.Handled = true;
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
