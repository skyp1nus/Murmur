using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Murmur.App.ViewModels;

/// <summary>
/// The keyboard shortcuts of the recording (Idea4Transcript.dc.html, "Idea 4 / Keyboard shortcuts bar", #61): what the keys do, as commands the
/// view's one key router (<see cref="Views.SessionShortcuts"/>) calls. Space is <c>PlayPauseCommand</c>, M is
/// <see cref="AddBookmarkAtPlayheadCommand"/> (#59), F is <see cref="ToggleFollowAlongCommand"/> (#56); the seeks and the line moves are here.
/// All of them go through <see cref="SeekTo"/>, so the audio, the video, the follow-along and the waveform follow.
/// </summary>
/// <remarks>
/// The design demo scene hides the bar (<see cref="ShowShortcuts"/> = <c>false</c>): the Session boards do not show it (see "design parity" in
/// docs/design/README.md).
/// </remarks>
public sealed partial class SessionViewModel
{
    /// <summary>How far J and L seek.</summary>
    internal static readonly TimeSpan ShortcutSeekStep = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The shortcuts bar may be shown. Always on, except in the design demo scene: the Session boards (Session.dc.html) do not have it, so the
    /// harness sets it to <c>false</c> (the same kind of switch as <see cref="ShowFollowAlong"/>).
    /// </summary>
    [ObservableProperty]
    public partial bool ShowShortcuts { get; set; } = true;

    /// <summary>J: 5 seconds back (not before the start).</summary>
    [RelayCommand]
    private void SeekBack() => SeekTo(Position - ShortcutSeekStep);

    /// <summary>L: 5 seconds forward (not past the end).</summary>
    [RelayCommand]
    private void SeekForward() => SeekTo(Position + ShortcutSeekStep);

    /// <summary>
    /// Up: the start of the line before the one at the playhead (on the first line, its own start). Nothing before the first line or without
    /// a transcript. The seek scrolls the list to the line.
    /// </summary>
    [RelayCommand]
    private void PreviousLine()
    {
        var rows = Segments;
        if (_timeline is not { } timeline || rows.Count == 0)
        {
            return;
        }

        var current = timeline.FindSegment(Position);
        if (current < 0)
        {
            return;
        }

        SeekToLine(rows, Math.Max(0, current - 1));
    }

    /// <summary>Down: the start of the line after the one at the playhead; the first line from before it; nothing after the last line.</summary>
    [RelayCommand]
    private void NextLine()
    {
        var rows = Segments;
        if (_timeline is not { } timeline || rows.Count == 0)
        {
            return;
        }

        var next = timeline.FindSegment(Position) + 1;
        if (next >= rows.Count)
        {
            return;
        }

        SeekToLine(rows, next);
    }

    private void SeekToLine(IReadOnlyList<SegmentViewModel> rows, int index) => SeekTo(rows[index].Start);

    /// <summary>
    /// Esc, first step: closes the popover that is open on this page (the "Did you mean" popover of an uncertain word, #58; the rename popover of a speaker, a bookmark's note box) and says whether
    /// there was one. Only when nothing was open does Esc go back to the library.
    /// </summary>
    internal bool CloseOpenPopover()
    {
        var closed = false;
        if (IsDidYouMeanOpen)
        {
            CloseDidYouMean();
            closed = true;
        }

        foreach (var chip in SpeakerChips)
        {
            if (chip.IsEditing)
            {
                chip.CancelCommand.Execute(null);
                closed = true;
            }
        }

        foreach (var bookmark in Bookmarks)
        {
            if (bookmark.IsEditing)
            {
                bookmark.CancelEditCommand.Execute(null);
                closed = true;
            }
        }

        return closed;
    }
}
