using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>
/// One marked moment of a recording (#59), shown in the Bookmarks card (time, note, edit, delete), as the pill between the transcript
/// lines and as a flag above the waveform. The note is edited in place: <see cref="EditText"/> is what the box holds, Enter
/// (<see cref="CommitEditCommand"/>) saves it, Esc (<see cref="CancelEditCommand"/>) drops it.
/// </summary>
public sealed partial class BookmarkViewModel : ObservableObject
{
    private readonly Action<TimeSpan> _seek;
    private readonly Func<BookmarkViewModel, string, Task> _save;
    private readonly Func<BookmarkViewModel, Task> _delete;
    private RelayCommand? _seekCommand;
    private RelayCommand? _beginEditCommand;
    private RelayCommand? _commitEditCommand;
    private RelayCommand? _cancelEditCommand;
    private RelayCommand? _deleteCommand;

    internal BookmarkViewModel(Bookmark bookmark, Action<TimeSpan> seek, Func<BookmarkViewModel, string, Task> save, Func<BookmarkViewModel, Task> delete)
    {
        Id = bookmark.Id;
        At = bookmark.At;
        TimeText = UiFormat.Clock(bookmark.At);
        Note = bookmark.Note;
        _seek = seek;
        _save = save;
        _delete = delete;
    }

    public long Id { get; }

    /// <summary>Where the moment is on the session timeline.</summary>
    public TimeSpan At { get; }

    /// <summary>"12:41", "1:02:03" (Geist Mono).</summary>
    public string TimeText { get; }

    /// <summary>The saved note; empty when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote), nameof(DisplayNote), nameof(ToolTip))]
    public partial string Note { get; internal set; }

    public bool HasNote => Note.Length > 0;

    /// <summary>The note, or the "Add a note" placeholder (shown in a lighter colour) while there is none.</summary>
    public string DisplayNote => HasNote ? Note : Strings.Session_BookmarkPlaceholder;

    /// <summary>"Go to 12:41": the tooltip of a row and a pill, which jump to the moment.</summary>
    public string GoToTip => UiText.Format(Strings.Session_BookmarkTip, TimeText);

    /// <summary>"Bookmark 12:41": the name of the row, the pill and the flag for screen readers.</summary>
    public string AutomationName => UiText.Format(Strings.Session_BookmarkName, TimeText);

    /// <summary>The time and the note (the flag's tooltip is built from the same two parts).</summary>
    public string ToolTip => HasNote ? $"{TimeText}  {Note}" : TimeText;

    /// <summary>The note box is open.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    /// <summary>What the note box holds while <see cref="IsEditing"/>.</summary>
    [ObservableProperty]
    public partial string EditText { get; set; } = string.Empty;

    /// <summary>The row, the flag and the pill: seek (audio and video) to the moment.</summary>
    public ICommand SeekCommand => _seekCommand ??= new RelayCommand(() => _seek(At));

    /// <summary>Opens the note box with the saved note.</summary>
    public ICommand BeginEditCommand => _beginEditCommand ??= new RelayCommand(BeginEdit);

    /// <summary>Enter: saves <see cref="EditText"/> (trimmed) as the note and closes the box.</summary>
    public ICommand CommitEditCommand => _commitEditCommand ??= new RelayCommand(CommitEdit);

    /// <summary>Esc: closes the box and keeps the saved note.</summary>
    public ICommand CancelEditCommand => _cancelEditCommand ??= new RelayCommand(CancelEdit);

    /// <summary>The × of the row: removes the bookmark everywhere.</summary>
    public ICommand DeleteCommand => _deleteCommand ??= new RelayCommand(() => _ = _delete(this));

    /// <summary>The last save or delete this row started (for tests).</summary>
    internal Task LastOperation { get; private set; } = Task.CompletedTask;

    private void BeginEdit()
    {
        EditText = Note;
        IsEditing = true;
    }

    private void CommitEdit()
    {
        if (!IsEditing)
        {
            return;
        }

        IsEditing = false;
        var note = EditText.Trim();
        if (note != Note)
        {
            LastOperation = _save(this, note);
        }
    }

    private void CancelEdit()
    {
        IsEditing = false;
        EditText = Note;
    }
}
