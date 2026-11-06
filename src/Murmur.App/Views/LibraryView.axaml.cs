using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Murmur.App.Services;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

public partial class LibraryView : UserControl
{
    private LibraryViewModel? _library;
    private ImportDialog? _importDialog;

    public LibraryView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTabInRows, RoutingStrategies.Tunnel);
    }

    /// <summary>The import dialog that is open (for tests), or <c>null</c>.</summary>
    internal ImportDialog? OpenImportDialog => _importDialog is { IsOpen: true } ? _importDialog : null;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Follow(DataContext as LibraryViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Follow(DataContext as LibraryViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Going to another screen takes the dialog with it.
        _importDialog?.Close();
        _importDialog = null;
        Follow(null);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Listens to the "Import files" pill of <paramref name="library"/> and gives it the file dialog of this window.</summary>
    private void Follow(LibraryViewModel? library)
    {
        if (ReferenceEquals(_library, library))
        {
            return;
        }

        if (_library is not null)
        {
            _library.ImportDialogRequested -= OnImportDialogRequested;
        }

        _library = library;
        if (library is not null)
        {
            library.ImportDialogRequested += OnImportDialogRequested;
            library.MediaFilePicker = new TopLevelMediaFilePicker(() => TopLevel.GetTopLevel(this));
        }
    }

    /// <summary>"Import files": the dialog opens over the window, and the focus returns to the pill when it closes.</summary>
    private void OnImportDialogRequested(ImportDialogViewModel dialog)
    {
        _importDialog?.Close();
        _importDialog = ImportDialog.Show(this, dialog);
        if (_importDialog is null)
        {
            // Not in a window (nothing to show it on): the dialog is dropped, nothing was imported.
            dialog.Dispose();
        }
    }

    /// <summary>
    /// Tab (and Shift+Tab) from a recording goes to the next (previous) one even when its row is not built yet: the list is virtualised,
    /// so the next row is scrolled into view and built first, and the focus does not jump to the end of the page or wrap around.
    /// </summary>
    private void OnTabInRows(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || (e.KeyModifiers & ~KeyModifiers.Shift) != 0)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Button { DataContext: SessionItemViewModel or SearchResultViewModel } current
            || current.FindAncestorOfType<ContentPresenter>() is not { } container)
        {
            return;
        }

        var index = Page.IndexFromContainer(container);
        if (index < 0)
        {
            return;
        }

        var step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1;
        var next = index + step;
        while (next >= 0 && next < Page.ItemCount && Page.Items[next] is not (SessionItemViewModel or SearchResultViewModel))
        {
            next += step;
        }

        if (next < 0 || next >= Page.ItemCount)
        {
            return;
        }

        e.Handled = true;
        Page.ScrollIntoView(next);
        Page.UpdateLayout();
        if (Page.ContainerFromIndex(next) is { } target
            && target.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("libraryRow")) is { } row)
        {
            row.Focus(NavigationMethod.Tab);
        }
    }
}
