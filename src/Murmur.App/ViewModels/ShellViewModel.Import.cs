using Murmur.Core.Import;
using Murmur.Core.Recording;

namespace Murmur.App.ViewModels;

/// <summary>Files dropped on the window (#69): the drop overlay, and the import dialog they are added to.</summary>
public sealed partial class ShellViewModel
{
    private ImportDialogViewModel? _openImportDialog;

    /// <summary>The overlay of files dragged over the window, or <c>null</c> when the app cannot import (nothing is accepted then).</summary>
    public DropOverlayViewModel? DropOverlay { get; private set; }

    /// <summary>
    /// A drop opened a new import dialog (files already added): the window shows it over everything and disposes it when it cannot.
    /// The dialog closes itself (<see cref="ImportDialogViewModel.CloseRequested"/>), and when the page changes.
    /// </summary>
    public event Action<ImportDialogViewModel>? ImportDialogRequested;

    /// <summary>The import dialog on screen (opened by a drop or by the Library's pill), or <c>null</c>.</summary>
    internal ImportDialogViewModel? OpenImportDialog => _openImportDialog is { IsClosed: false, IsDisposed: false } ? _openImportDialog : null;

    private void InitializeImport(LibraryViewModel library, IImportQueue? imports)
    {
        // The dialog of the Library's pill takes the files of a drop too, so there is never a second one.
        library.ImportDialogRequested += dialog => _openImportDialog = dialog;
        if (imports is null)
        {
            return;
        }

        DropOverlay = new DropOverlayViewModel(
            imports.SupportedExtensions,
            () => Recording.State != RecorderState.Idle,
            () => CurrentPage is not OnboardingViewModel && !_closed,
            () => OpenImportDialog is not null);
        DropOverlay.FilesDropped += OnFilesDropped;
    }

    /// <summary>Going to another page takes the import dialog with it, as it does for the dialog of the pill.</summary>
    private void CloseImportDialog() => OpenImportDialog?.CloseCommand.Execute(null);

    /// <summary>Files were dropped: they join the open import dialog, else a new one (the Library's) opens with them.</summary>
    private void OnFilesDropped(IReadOnlyList<string> files)
    {
        if (OpenImportDialog is { } open)
        {
            open.AddFiles(files);
            return;
        }

        if (Library.CreateImportDialog() is not { } dialog)
        {
            return;
        }

        dialog.AddFiles(files);
        _openImportDialog = dialog;
        if (ImportDialogRequested is { } requested)
        {
            requested(dialog);
        }
        else
        {
            dialog.Dispose();
            _openImportDialog = null;
        }
    }
}
