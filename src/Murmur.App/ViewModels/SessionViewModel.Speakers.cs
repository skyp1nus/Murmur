using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.App.Resources;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.ViewModels;

/// <summary>
/// Speaker names (Idea4Transcript.dc.html, "Idea 4 / Speakers"): the chips above the transcript, one per speaker who has lines, and what a
/// rename changes: the label and the avatar letter of every line of this recording, and the labels of an export. The names are kept by
/// <see cref="ISpeakerNameStore"/> (the AI reads them from there). The row is offered only while the store is wired (<c>null</c> = the lines
/// say "You" / "Others" and nothing can be renamed): the design demo scene builds the Session boards without it, because the boards do not
/// show the row (see "design parity" in docs/design/README.md).
/// </summary>
public sealed partial class SessionViewModel
{
    private SpeakerNames _speakerNames = SpeakerNames.Default;

    /// <summary>The chips (the microphone speaker first); empty while there is no transcript with lines.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSpeakerChips))]
    public partial IReadOnlyList<SpeakerChipViewModel> SpeakerChips { get; private set; } = [];

    /// <summary>The row of chips is on screen: the transcript list is, and there is a store to keep the names.</summary>
    public bool ShowSpeakerChips => _services.SpeakerNames is not null && ShowTranscriptList && SpeakerChips.Count > 0;

    /// <summary>The names of the speakers of this recording (the defaults until the user chooses).</summary>
    internal SpeakerNames SpeakerNames => _speakerNames;

    /// <summary>Reads the stored names; a store that cannot be read means the default labels.</summary>
    private async Task LoadSpeakerNamesAsync()
    {
        if (_services.SpeakerNames is not { } store)
        {
            return;
        }

        try
        {
            _speakerNames = await store.GetSpeakerNamesAsync(Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _speakerNames = SpeakerNames.Default;
        }
    }

    /// <summary>Builds the chips of the speakers that have lines in <paramref name="segments"/>.</summary>
    private void BuildSpeakerChips(IReadOnlyList<TranscriptSegment> segments)
    {
        var me = 0;
        var others = 0;
        foreach (var segment in segments)
        {
            if (segment.Speaker == Speaker.Me)
            {
                me++;
            }
            else
            {
                others++;
            }
        }

        var chips = new List<SpeakerChipViewModel>(2);
        if (me > 0)
        {
            chips.Add(new SpeakerChipViewModel(Speaker.Me, _speakerNames.Me, me, RenameSpeakerAsync));
        }

        if (others > 0)
        {
            chips.Add(new SpeakerChipViewModel(Speaker.Others, _speakerNames.Others, others, RenameSpeakerAsync));
        }

        SpeakerChips = chips;
    }

    /// <summary>
    /// Stores the name (<c>null</c> or blank = the default) and shows what the store kept (it trims and cuts the name) on every line, on the
    /// chip and in the next export. A store that fails leaves the old name and says so in <see cref="ActionMessage"/>.
    /// </summary>
    internal async Task RenameSpeakerAsync(Speaker speaker, string? name)
    {
        if (_services.SpeakerNames is not { } store)
        {
            return;
        }

        try
        {
            await store.SetSpeakerNameAsync(Id, speaker, name, CancellationToken.None).ConfigureAwait(true);
            _speakerNames = await store.GetSpeakerNamesAsync(Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or IOException or InvalidOperationException)
        {
            ActionMessage = UiText.Format(Strings.Speakers_RenameFailed, ex.Message);
            return;
        }

        if (_disposed)
        {
            return;
        }

        ApplySpeakerNames();
    }

    /// <summary>Puts <see cref="SpeakerNames"/> on every line and every chip.</summary>
    private void ApplySpeakerNames()
    {
        foreach (var row in Segments)
        {
            row.SpeakerName = _speakerNames[row.Speaker];
        }

        foreach (var chip in SpeakerChips)
        {
            chip.Apply(_speakerNames[chip.Speaker]);
        }
    }
}
