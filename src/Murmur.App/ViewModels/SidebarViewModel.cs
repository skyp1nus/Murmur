using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>What the list under the pages shows.</summary>
public enum SidebarContext
{
    None,

    /// <summary>The three latest recordings (Library and the recording view).</summary>
    Recent,

    /// <summary>The chats of Ask AI; filled by the chat screen through <see cref="SidebarViewModel.SetChats"/>.</summary>
    Chats,

    /// <summary>The sections of the Settings page.</summary>
    Sections,
}

/// <summary>
/// A row of the sidebar's context list: a pill with a title, highlighted while it is the open one. A chat row can also be renamed
/// (inline, in place of the pill) and deleted from its context menu.
/// </summary>
public sealed partial class SidebarEntry : ObservableObject
{
    private readonly Action? _open;
    private readonly Action<string>? _rename;
    private readonly Action? _delete;

    /// <param name="title">The text of the pill.</param>
    /// <param name="open">Clicking the pill.</param>
    /// <param name="isActive">The open recording, chat or section.</param>
    /// <param name="rename">Renames the row to the (trimmed, non-empty, changed) text; <c>null</c> = it cannot be renamed.</param>
    /// <param name="delete">Deletes what the row stands for; <c>null</c> = it cannot be deleted.</param>
    public SidebarEntry(string title, Action? open = null, bool isActive = false, Action<string>? rename = null, Action? delete = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        Title = title;
        _open = open;
        _rename = rename;
        _delete = delete;
        IsActive = isActive;
        EditText = title;
    }

    public string Title { get; }

    /// <summary>The row has a context menu (Rename, Delete).</summary>
    public bool CanManage => _rename is not null || _delete is not null;

    public bool CanRename => _rename is not null;

    public bool CanDelete => _delete is not null;

    /// <summary>The open recording, chat or section: the pill is filled and its text is medium.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>The title is being edited in place of the pill.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotEditing))]
    public partial bool IsEditing { get; private set; }

    public bool IsNotEditing => !IsEditing;

    /// <summary>The text of the inline editor.</summary>
    [ObservableProperty]
    public partial string EditText { get; set; }

    [RelayCommand]
    private void Open() => _open?.Invoke();

    [RelayCommand]
    private void BeginRename()
    {
        if (_rename is null)
        {
            return;
        }

        EditText = Title;
        IsEditing = true;
    }

    /// <summary>Enter in the editor: renames when the text is not blank and changed.</summary>
    [RelayCommand]
    private void CommitRename()
    {
        if (!IsEditing)
        {
            return;
        }

        IsEditing = false;
        var text = EditText.Trim();
        if (text.Length > 0 && text != Title)
        {
            _rename?.Invoke(text);
        }
    }

    /// <summary>Escape in the editor.</summary>
    [RelayCommand]
    private void CancelRename() => IsEditing = false;

    [RelayCommand]
    private void Delete() => _delete?.Invoke();
}

/// <summary>
/// The left navigation of the canvas: the wordmark, "New recording" (the "Recording" pill while a recording runs), Library, Ask AI,
/// Settings, a list that depends on the screen (<see cref="Context"/>) and the "Fully offline" card. It follows the page the shell shows.
/// </summary>
/// <remarks>
/// The list is <b>Recent</b> on the Library and the open recording (the open one highlighted), <b>Chats</b> on Ask AI and
/// <b>Sections</b> on Settings. The chat screen fills the chats through <see cref="SetChats"/>; the settings screen's sections come
/// from <see cref="SettingsViewModel.Sections"/> and a click scrolls the page there through its <c>ShowSectionCommand</c>.
/// </remarks>
public sealed partial class SidebarViewModel : ObservableObject
{
    /// <summary>How many recent recordings the list shows.</summary>
    public const int RecentCount = 3;

    private readonly ShellViewModel _shell;
    private IReadOnlyList<SidebarEntry> _chats = [];

    internal SidebarViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Entries = [];
        Rebuild();
    }

    /// <summary>The library is the current place: its own screen, or a recording opened from it.</summary>
    public bool IsLibrary => _shell.CurrentPage is LibraryViewModel or SessionViewModel;

    public bool IsChat => _shell.CurrentPage is LibraryChatViewModel;

    public bool IsSettings => _shell.CurrentPage is SettingsViewModel;

    public bool CanAskAi => _shell.CanShowLibraryChat;

    /// <summary>A recording is running (or starting, or stopping): the "Recording" pill replaces "New recording".</summary>
    public bool IsRecordingActive => _shell.Recording.State != RecorderState.Idle;

    public bool ShowNewRecording => !IsRecordingActive;

    /// <summary>The recording view is on screen: the pill is the current place.</summary>
    public bool IsOnRecordingPage => _shell.CurrentPage is RecordingViewModel;

    /// <summary>"Recordings and models stay on this PC", or the promise that matters while recording.</summary>
    public string OfflineText => IsRecordingActive ? Strings.Nav_OfflineRecording : Strings.Nav_OfflineText;

    /// <summary>The Settings screen of the canvas has no offline card.</summary>
    public bool ShowOffline => !IsSettings;

    public SidebarContext Context =>
        _shell.CurrentPage switch
        {
            LibraryViewModel or SessionViewModel => SidebarContext.Recent,
            LibraryChatViewModel => SidebarContext.Chats,
            SettingsViewModel => SidebarContext.Sections,
            _ => SidebarContext.None,
        };

    public string? ContextTitle => Context switch
    {
        SidebarContext.Recent => Strings.Nav_Recent,
        SidebarContext.Chats => Strings.Nav_Chats,
        SidebarContext.Sections => Strings.Nav_Sections,
        _ => null,
    };

    /// <summary>The rows of the list; replaced as a whole when the page or the data changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntries))]
    public partial IReadOnlyList<SidebarEntry> Entries { get; private set; }

    /// <summary>The list has rows (an empty one hides its heading too).</summary>
    public bool HasEntries => Entries.Count > 0;

    /// <summary>The sections of the Settings page (kept for the settings tests; the list shows them as <see cref="Entries"/>).</summary>
    public IReadOnlyList<SettingsSection> Sections => _shell.Settings?.Sections ?? [];

    /// <summary>
    /// The chat screen's hook: the chats the Chats list shows (the open one with <see cref="SidebarEntry.IsActive"/> set).
    /// Until a chat screen provides them the list is empty and hidden.
    /// </summary>
    public void SetChats(IReadOnlyList<SidebarEntry> chats)
    {
        ArgumentNullException.ThrowIfNull(chats);
        _chats = chats;
        if (Context == SidebarContext.Chats)
        {
            Rebuild();
        }
    }

    [RelayCommand]
    private void NewRecording() => _shell.ShowRecording();

    [RelayCommand]
    private void ShowRecording() => _shell.ShowRecording();

    [RelayCommand]
    private void ShowLibrary() => _shell.ShowLibrary();

    [RelayCommand]
    private void ShowChat() => _shell.ShowNewChat();

    [RelayCommand]
    private void ShowSettings() => _shell.ShowSettings();

    [RelayCommand]
    private void ShowSection(SettingsSection? section) => _shell.Settings?.ShowSectionCommand.Execute(section);

    /// <summary>The shell moved to another page, or something the sidebar shows changed (recorder state, recent recordings).</summary>
    internal void Update()
    {
        OnPropertyChanged(nameof(IsLibrary));
        OnPropertyChanged(nameof(IsChat));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(CanAskAi));
        OnPropertyChanged(nameof(IsRecordingActive));
        OnPropertyChanged(nameof(ShowNewRecording));
        OnPropertyChanged(nameof(IsOnRecordingPage));
        OnPropertyChanged(nameof(OfflineText));
        OnPropertyChanged(nameof(ShowOffline));
        OnPropertyChanged(nameof(Context));
        OnPropertyChanged(nameof(ContextTitle));
        OnPropertyChanged(nameof(Sections));
        Rebuild();
    }

    private void Rebuild()
    {
        var entries = new List<SidebarEntry>();
        switch (Context)
        {
            case SidebarContext.Recent:
                var open = _shell.Session?.Id;
                foreach (var record in _shell.Library.Recent.Take(RecentCount))
                {
                    var captured = record;
                    entries.Add(new SidebarEntry(
                        string.IsNullOrWhiteSpace(record.Title) ? Strings.Common_UntitledRecording : record.Title,
                        () => _shell.OpenSession(captured),
                        isActive: open is { } id && id == record.Id));
                }

                break;

            case SidebarContext.Chats:
                entries.AddRange(_chats);
                break;

            case SidebarContext.Sections:
                foreach (var section in Sections)
                {
                    var captured = section;
                    entries.Add(new SidebarEntry(section.Title, () => ShowSection(captured)));
                }

                break;
        }

        Entries = entries;
    }
}
