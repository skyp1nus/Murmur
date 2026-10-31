using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTabInRows, RoutingStrategies.Tunnel);
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
