using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

public partial class SessionActionsView : UserControl
{
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

    /// <summary>A menu item was chosen (its command has run): the menu closes.</summary>
    private void OnMenuItemClick(object? sender, RoutedEventArgs e)
    {
        ExportButton.Flyout?.Hide();
        MoreButton.Flyout?.Hide();
    }
}
