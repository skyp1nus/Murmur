using Avalonia.Controls;
using Avalonia.Input;
using Avalonia;
using Avalonia.Interactivity;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

public partial class SessionActionsView : UserControl
{
    private SessionViewModel? _session;
    private ExportDialog? _dialog;

    public SessionActionsView()
    {
        InitializeComponent();
        // Enter in the rename box saves and closes the menu, like the Save button.
        RenameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is SessionActionsViewModel { ApplyRenameCommand: var apply })
            {
                apply.Execute(null);
                MoreButton.Flyout?.Hide();
                e.Handled = true;
            }
        };
        if (MoreButton.Flyout is { } more)
        {
            more.Closed += (_, _) => (DataContext as SessionActionsViewModel)?.ResetMenu();
        }
    }

    /// <summary>The export dialog that is open (for tests), or <c>null</c>.</summary>
    internal ExportDialog? OpenDialog => _dialog is { IsOpen: true } ? _dialog : null;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_session is not null)
        {
            _session.ExportDialogRequested -= OnExportDialogRequested;
        }

        _session = (DataContext as SessionActionsViewModel)?.Session;
        if (_session is not null)
        {
            _session.ExportDialogRequested += OnExportDialogRequested;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_session is null && DataContext is SessionActionsViewModel { Session: var session })
        {
            _session = session;
            _session.ExportDialogRequested += OnExportDialogRequested;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Going to another screen takes the dialog with it.
        _dialog?.Close();
        _dialog = null;
        if (_session is not null)
        {
            _session.ExportDialogRequested -= OnExportDialogRequested;
            _session = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The Export button: the dialog opens over the window, and the focus returns to the button when it closes.</summary>
    private void OnExportDialogRequested(ExportDialogViewModel dialog)
    {
        _dialog?.Close();
        _dialog = ExportDialog.Show(ExportButton, dialog);
    }

    /// <summary>A menu item was chosen (its command has run): the menu closes.</summary>
    private void OnMenuItemClick(object? sender, RoutedEventArgs e) => MoreButton.Flyout?.Hide();
}
