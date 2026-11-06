using Murmur.App.Services;
using Murmur.Core.Import;
using Murmur.Core.Recording;

namespace Murmur.App.ViewModels;

/// <summary>Files dropped on the window (#69): the drop overlay, and the import dialog they are added to.</summary>
public sealed partial class ShellViewModel
{
    private Func<IMediaFilePicker?, ImportDialogViewModel>? _importDialogFactory;
    private ImportDialogViewModel? _openImportDialog;

    /// <summary>The overlay of files dragged over the window, or <c>null</c> when the app cannot import (nothing is accepted then).</summary>
    public DropOverlayViewModel? DropOverlay { get; private set; }

    /// <summary>The file dialog of the import's "browse this PC" for dialogs the shell opens (the window sets it).</summary>
    public IMediaFilePicker? MediaFilePicker { get; set; }

    /// <summary>
    /// A drop opened a new import dialog (files already added): the window shows it over everything and disposes it when it cannot.
    /// The dialog closes itself (<see cref="ImportDialogViewModel.CloseRequested"/>).
    /// </summary>
    public event Action<ImportDialogViewModel>? ImportDialogRequested;

    /// <summary>The import dialog on screen (opened by a drop or by the Library's pill), or <c>null</c>.</summary>
    internal ImportDialogViewModel? OpenImportDialog => _openImportDialog is { IsClosed: false, IsDisposed: false } ? _openImportDialog : null;

    private void InitializeImport(LibraryViewModel library, IImportQueue? imports, Func<IMediaFilePicker?, ImportDialogViewModel>? factory)
    {
        // The dialog of the Library's pill takes the files of a drop too, so there is never a second one.
        library.ImportDialogRequested += dialog => _openImportDialog = dialog;
        if (imports is null)
        {
            return;
        }

        _importDialogFactory = factory;
        DropOverlay = new DropOverlayViewModel(
            imports.SupportedExtensions,
            () => Recording.State != RecorderState.Idle,
            () => CurrentPage is not OnboardingViewModel && !_closed);
        DropOverlay.FilesDropped += OnFilesDropped;
    }

    /// <summary>Files were dropped: they join the open import dialog, else a new one opens with them.</summary>
    private void OnFilesDropped(IReadOnlyList<string> files)
    {
        if (OpenImportDialog is { } open)
        {
            open.AddFiles(files);
            return;
        }

        if (_importDialogFactory is not { } factory)
        {
            return;
        }

        var dialog = factory(MediaFilePicker);
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
