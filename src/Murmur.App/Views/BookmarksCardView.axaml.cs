using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

/// <summary>
/// The Bookmarks card under the player (see <c>BookmarksCardView.axaml</c>). The code here is only what XAML cannot say: the note box takes
/// the focus when it opens, a click elsewhere saves what was typed, and a double click on a note opens it for editing.
/// </summary>
public partial class BookmarksCardView : UserControl
{
    public BookmarksCardView()
    {
        InitializeComponent();
    }

    /// <summary>The box of a row is created hidden and shown by "edit": it takes the focus, with its text selected, the moment it appears.</summary>
    private void OnEditorLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.PropertyChanged -= OnEditorPropertyChanged;
            box.PropertyChanged += OnEditorPropertyChanged;
        }
    }

    private static void OnEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && e.NewValue is true && sender is TextBox box)
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    box.Focus();
                    box.SelectAll();
                },
                DispatcherPriority.Input);
        }
    }

    private void OnNoteDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BookmarkViewModel row)
        {
            row.BeginEditCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Leaving the box saves the note like Enter does (Esc has already closed the box, so it is not saved).</summary>
    private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is BookmarkViewModel { IsEditing: true } row)
        {
            row.CommitEditCommand.Execute(null);
        }
    }
}
