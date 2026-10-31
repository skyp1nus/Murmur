using Avalonia.Controls;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
    }

    /// <summary>The inline editor of a chat lost the focus: what was typed is the new name (Escape has cancelled before this).</summary>
    private void OnRenameFinished(object? sender, EventArgs e)
    {
        if (sender is Control { DataContext: SidebarEntry { IsEditing: true } entry })
        {
            entry.CommitRenameCommand.Execute(null);
        }
    }
}
