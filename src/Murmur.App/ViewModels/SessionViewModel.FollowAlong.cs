using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Murmur.App.ViewModels;

/// <summary>
/// "Follow along" (Idea4Transcript.dc.html, "Idea 4 / Active line – follow along", #56): the switch in the transcript header. While it is
/// on and the recording plays, the word being spoken is marked in its line and the list keeps the current line about a third of the way
/// down; when it is off, only the filled current line stays and the list is not moved. The choice is kept in
/// <see cref="Murmur.Core.Settings.AppSettings.FollowAlong"/> (through <see cref="SettingsWriter"/>) for every recording.
/// </summary>
/// <remarks>
/// The design demo scene turns the switch off and hides it (<see cref="ShowFollowAlong"/> = <c>false</c>, <see cref="FollowAlong"/> =
/// <c>false</c>): the Session boards show the filled line but neither the switch nor the word chip (see "design parity" in
/// docs/design/README.md). The F key that toggles it belongs to the keyboard shortcuts (#61): it binds <see cref="ToggleFollowAlongCommand"/>.
/// </remarks>
public sealed partial class SessionViewModel
{
    private bool _loadingFollowAlong;

    /// <summary>The spoken word is marked and the list follows the playback.</summary>
    [ObservableProperty]
    public partial bool FollowAlong { get; set; } = true;

    /// <summary>
    /// The switch may be shown. Always on, except in the design demo scene: the Session boards (Session.dc.html) do not have it, so the
    /// harness sets it to <c>false</c> (the same kind of switch as <see cref="ShowSpeakers"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFollowAlongSwitch))]
    public partial bool ShowFollowAlong { get; set; } = true;

    /// <summary>The switch is on screen: it is allowed and the transcript list is on screen.</summary>
    public bool ShowFollowAlongSwitch => ShowFollowAlong && ShowTranscriptList;

    /// <summary>The last save of the switch (for tests).</summary>
    internal Task LastFollowAlongSave { get; private set; } = Task.CompletedTask;

    /// <summary>Flips <see cref="FollowAlong"/>; the F key of the shortcuts (#61) is bound to it.</summary>
    [RelayCommand]
    private void ToggleFollowAlong() => FollowAlong = !FollowAlong;

    private void LoadFollowAlong()
    {
        if (_services.Settings is not { } store)
        {
            return;
        }

        _loadingFollowAlong = true;
        try
        {
            FollowAlong = SettingsWriter.For(store).Current.FollowAlong;
        }
        finally
        {
            _loadingFollowAlong = false;
        }
    }

    partial void OnFollowAlongChanged(bool value)
    {
        // The word is marked only while it plays and the switch is on.
        var showWord = value && IsPlaying;
        foreach (var row in Segments)
        {
            row.ShowWord = showWord;
        }

        if (value)
        {
            // Back on: the list returns to the line that is playing, whatever the user scrolled before.
            _scrollPaused = false;
            if (_currentSegment >= 0)
            {
                ScrollToSegmentRequested?.Invoke(_currentSegment);
            }
        }

        if (!_loadingFollowAlong && _services.Settings is { } store)
        {
            LastFollowAlongSave = SaveFollowAlongAsync(store, value);
        }
    }

    private static async Task SaveFollowAlongAsync(Murmur.Core.Settings.ISettingsStore store, bool value)
    {
        // A store that cannot be written keeps the switch as the user set it for this view; there is no place for the error in the header.
        await SettingsWriter.For(store).UpdateAsync(s => s with { FollowAlong = value }).ConfigureAwait(true);
    }
}
