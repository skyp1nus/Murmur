using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Murmur.App.ViewModels;

/// <summary>What the "…" menu shows.</summary>
public enum SessionMenuMode
{
    /// <summary>Rename, Show in folder, Re-transcribe, Delete.</summary>
    Menu,

    /// <summary>A box with the title and Save.</summary>
    Rename,

    /// <summary>"Delete this recording?" with Delete and Cancel.</summary>
    ConfirmDelete,
}

/// <summary>
/// The right side of the top bar of a recording: the <b>Export</b> menu and the <b>…</b> menu (Rename, Show in folder,
/// Re-transcribe, Delete). The window shows it through a data template, so the buttons sit in the 60 px bar next to the breadcrumb.
/// The commands belong to the <see cref="Session"/>; this holds only what the menus need to remember.
/// </summary>
public sealed partial class SessionActionsViewModel : ObservableObject
{
    internal SessionActionsViewModel(SessionViewModel session)
    {
        Session = session;
        RenameText = session.Title;
    }

    public SessionViewModel Session { get; }

    /// <summary>The content of the "…" menu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMenu))]
    [NotifyPropertyChangedFor(nameof(IsRename))]
    [NotifyPropertyChangedFor(nameof(IsConfirmDelete))]
    public partial SessionMenuMode Mode { get; private set; } = SessionMenuMode.Menu;

    /// <summary>The text of the rename box.</summary>
    [ObservableProperty]
    public partial string RenameText { get; set; }

    public bool IsMenu => Mode == SessionMenuMode.Menu;

    public bool IsRename => Mode == SessionMenuMode.Rename;

    public bool IsConfirmDelete => Mode == SessionMenuMode.ConfirmDelete;

    /// <summary>The menu closed (or is about to open): it starts at the list again.</summary>
    public void ResetMenu()
    {
        Mode = SessionMenuMode.Menu;
        RenameText = Session.Title;
    }

    [RelayCommand]
    private void BeginRename()
    {
        RenameText = Session.Title;
        Mode = SessionMenuMode.Rename;
    }

    /// <summary>Save: renames the recording; the menu goes back to the list (the view closes it).</summary>
    [RelayCommand]
    private void ApplyRename()
    {
        Session.StartRename(RenameText);
        Mode = SessionMenuMode.Menu;
    }

    [RelayCommand]
    private void BeginDelete() => Mode = SessionMenuMode.ConfirmDelete;

    [RelayCommand]
    private void ConfirmDelete()
    {
        Mode = SessionMenuMode.Menu;
        Session.StartDelete();
    }

    [RelayCommand]
    private void Back() => Mode = SessionMenuMode.Menu;
}
