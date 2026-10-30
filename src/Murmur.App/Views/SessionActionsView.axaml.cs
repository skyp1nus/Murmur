using Avalonia.Controls;
using Avalonia.Interactivity;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

public partial class SessionActionsView : UserControl
{
    public SessionActionsView()
    {
        InitializeComponent();
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
