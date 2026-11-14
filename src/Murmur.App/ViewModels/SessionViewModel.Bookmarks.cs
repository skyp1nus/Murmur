using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Controls;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>
/// The marked moments of the recording (#45) shown where the recording is reviewed (#59): a flag above the waveform, a row in the
/// Bookmarks card under the player and a dashed pill between the transcript lines. All three show the same <see cref="BookmarkViewModel"/>
/// rows, so an edit or a delete is seen everywhere at once, and a click on any of them seeks the audio and the video (<see cref="SeekTo"/>,
/// the path the chapters use).
/// </summary>
/// <remarks>
/// <para>The card (and with it the flags and the pills) exists only when the composition root gave the view the store
/// (<see cref="Services.SessionServices.Bookmarks"/>): the design demo scene has none, so the Session boards stay as they were drawn.</para>
/// <para><b>Pills.</b> A pill is an own row between two transcript rows, never inside a line: it follows the row whose span
/// (its start up to the next row's start) holds the moment, so the words of a line never move (<see cref="SegmentViewModel.MarksAfter"/>);
/// a moment before the first line goes in front of it (<see cref="SegmentViewModel.MarksBefore"/>). Rows own the pills they show, so the
/// virtualised list needs no extra items and follow-along scrolls by the same row indexes as before.</para>
/// <para>The M key of the recording view and the keyboard shortcuts (#61) bind <see cref="AddBookmarkAtPlayheadCommand"/>.</para>
/// </remarks>
public sealed partial class SessionViewModel
{
    /// <summary>A bookmark closer than this to an existing (or just requested) one is the same moment: key repeat or a double M.</summary>
    private static readonly TimeSpan BookmarkDuplicateWindow = TimeSpan.FromSeconds(0.5);

    private BookmarkViewModel[] _bookmarkRows = [];
    private readonly List<TimeSpan> _bookmarksBeingAdded = [];
    private bool _bookmarksLoaded;

    /// <summary>The bookmarks this view added while the recording was being reviewed (M): they are not "marked during the call".</summary>
    private readonly HashSet<long> _reviewBookmarkIds = [];

    /// <summary>The bookmarks in timeline order (the card's rows); empty when there are none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBookmarks), nameof(ShowBookmarksEmpty), nameof(ShowBookmarksHint))]
    public partial IReadOnlyList<BookmarkViewModel> Bookmarks { get; private set; } = [];

    /// <summary>The flags above the waveform: the same bookmarks as positions on the waveform's width.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<WaveformFlag> BookmarkFlags { get; private set; } = [];

    public bool HasBookmarks => Bookmarks.Count > 0;

    /// <summary>The Bookmarks card is on screen: the recording has a bookmark store (it is hidden without one, like the design demo's).</summary>
    public bool ShowBookmarksCard => _services.Bookmarks is not null;

    /// <summary>The card shows "No bookmarks yet. Press M to add one." instead of rows.</summary>
    public bool ShowBookmarksEmpty => ShowBookmarksCard && _bookmarksLoaded && !HasBookmarks;

    /// <summary>
    /// "Marked during the call" next to the card title: shown only while there is a bookmark that was made during the call. A
    /// <see cref="Bookmark"/> stores no origin (id, session, time and note only), so the origin is judged here (<see cref="IsMarkedDuringTheCall"/>).
    /// </summary>
    public bool ShowBookmarksHint => ShowBookmarksCard && Array.Exists(_bookmarkRows, IsMarkedDuringTheCall);

    /// <summary>
    /// A proxy for "made while recording", as a bookmark carries no origin: the session was recorded (an imported file never had a call
    /// to mark), the bookmark was not added by M in this view, and its time lies inside the capture window (the length of the recording,
    /// with a second to spare for the rounding of the last mark). A mark added with M in an earlier visit to the recording cannot be told
    /// from a recorded one: that needs an origin on the stored bookmark.
    /// </summary>
    private bool IsMarkedDuringTheCall(BookmarkViewModel row)
    {
        if (_record.Source != SessionSource.Recorded || _reviewBookmarkIds.Contains(row.Id))
        {
            return false;
        }

        var window = _record.Duration > TimeSpan.Zero ? _record.Duration : Duration;
        return row.At >= TimeSpan.Zero && row.At <= window + TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Adds a bookmark at the playhead (the M key). The card, the flags and the pills show it as soon as the store has saved it; a store
    /// that cannot save leaves the list as it was. Without a store the command does nothing.
    /// </summary>
    [RelayCommand]
    private async Task AddBookmarkAtPlayheadAsync()
    {
        if (_services.Bookmarks is not { } store || _disposed)
        {
            return;
        }

        var at = Duration > TimeSpan.Zero ? Clamp(Position) : Position;
        if (_bookmarkRows.Any(r => (r.At - at).Duration() < BookmarkDuplicateWindow)
            || _bookmarksBeingAdded.Exists(a => (a - at).Duration() < BookmarkDuplicateWindow))
        {
            return;
        }

        _bookmarksBeingAdded.Add(at);
        try
        {
            var all = await store.AddBookmarksAsync(Id, [new NewBookmark(at)], CancellationToken.None).ConfigureAwait(true);
            if (!_disposed)
            {
                foreach (var bookmark in all)
                {
                    if (Array.TrueForAll(_bookmarkRows, r => r.Id != bookmark.Id))
                    {
                        _reviewBookmarkIds.Add(bookmark.Id);
                    }
                }

                ShowBookmarks(all);
            }
        }
        catch (Exception)
        {
            // A mark that cannot be saved is not shown; the list stays as it was.
        }
        finally
        {
            _bookmarksBeingAdded.Remove(at);
        }
    }

    private async Task LoadBookmarksAsync()
    {
        if (_disposed || _services.Bookmarks is not { } store)
        {
            return;
        }

        IReadOnlyList<Bookmark> bookmarks = [];
        try
        {
            bookmarks = await store.GetBookmarksAsync(Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Bookmarks that cannot be read are as good as none: the card says so.
        }

        if (!_disposed)
        {
            ShowBookmarks(bookmarks);
        }
    }

    /// <summary>Replaces the rows with these bookmarks (the rows of bookmarks that stay are kept) and places the flags and the pills.</summary>
    private void ShowBookmarks(IReadOnlyList<Bookmark> bookmarks)
    {
        var rows = new BookmarkViewModel[bookmarks.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            var bookmark = bookmarks[i];
            var row = Array.Find(_bookmarkRows, r => r.Id == bookmark.Id);
            if (row is null)
            {
                row = new BookmarkViewModel(bookmark, SeekToBookmark, SaveBookmarkNoteAsync, DeleteBookmarkAsync);
            }
            else if (!row.IsEditing)
            {
                row.Note = bookmark.Note;
            }

            rows[i] = row;
        }

        Array.Sort(rows, static (a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Id.CompareTo(b.Id));
        _bookmarkRows = rows;
        _bookmarksLoaded = true;
        Bookmarks = rows;
        RebuildBookmarkFlags();
        ApplyBookmarkMarks();
    }

    /// <summary>A click on a row, a flag or a pill: audio and video jump to the moment (play or pause stays as it is).</summary>
    private void SeekToBookmark(TimeSpan at) => SeekTo(at);

    /// <summary>The click on a flag above the waveform (index into <see cref="BookmarkFlags"/>).</summary>
    public void SeekToBookmarkFlag(int index)
    {
        if ((uint)index < (uint)_bookmarkRows.Length)
        {
            SeekToBookmark(_bookmarkRows[index].At);
        }
    }

    private async Task SaveBookmarkNoteAsync(BookmarkViewModel row, string note)
    {
        if (_services.Bookmarks is not { } store)
        {
            return;
        }

        var before = row.Note;
        row.Note = note;
        try
        {
            await store.UpdateBookmarkNoteAsync(row.Id, note, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Not saved (or gone): the row shows what is stored.
            if (!_disposed)
            {
                row.Note = before;
            }
        }

        if (!_disposed)
        {
            RebuildBookmarkFlags();
        }
    }

    private async Task DeleteBookmarkAsync(BookmarkViewModel row)
    {
        if (_services.Bookmarks is not { } store)
        {
            return;
        }

        try
        {
            await store.DeleteBookmarkAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Not removed: it stays everywhere.
            return;
        }

        if (_disposed)
        {
            return;
        }

        ShowBookmarks([.. _bookmarkRows.Where(r => r.Id != row.Id).Select(r => new Bookmark(r.Id, Id, r.At, r.Note))]);
    }

    private void RebuildBookmarkFlags()
    {
        var rows = _bookmarkRows;
        var flags = new WaveformFlag[rows.Length];
        for (var i = 0; i < flags.Length; i++)
        {
            var fraction = Duration > TimeSpan.Zero ? Math.Clamp(rows[i].At / Duration, 0, 1) : 0;
            flags[i] = new WaveformFlag(fraction, rows[i].TimeText, rows[i].Note);
        }

        BookmarkFlags = flags;
    }

    /// <summary>
    /// Hands every transcript row the pills that stand after it: those whose moment is in its span (from its start up to the start of the
    /// next row). Both lists are in timeline order, so one pass does it; a row whose pills did not change raises nothing.
    /// </summary>
    private void ApplyBookmarkMarks()
    {
        var rows = Segments;
        if (rows.Count == 0)
        {
            return;
        }

        var marks = _bookmarkRows;
        var next = 0;
        IReadOnlyList<BookmarkViewModel> before = [];
        if (marks.Length > 0 && marks[0].At < rows[0].Start)
        {
            var count = 0;
            while (count < marks.Length && marks[count].At < rows[0].Start)
            {
                count++;
            }

            before = marks[..count];
            next = count;
        }

        for (var s = 0; s < rows.Count; s++)
        {
            var limit = s + 1 < rows.Count ? rows[s + 1].Start : TimeSpan.MaxValue;
            var from = next;
            while (next < marks.Length && marks[next].At < limit)
            {
                next++;
            }

            rows[s].SetMarks(s == 0 ? before : [], next > from ? marks[from..next] : []);
        }
    }
}
