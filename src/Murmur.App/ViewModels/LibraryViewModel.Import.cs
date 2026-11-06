using CommunityToolkit.Mvvm.Input;
using Murmur.App.Services;
using Murmur.Core.Import;

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
}
