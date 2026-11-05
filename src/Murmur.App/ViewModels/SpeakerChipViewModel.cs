using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Transcription;

namespace Murmur.App.ViewModels;

/// <summary>
/// One chip of the speaker row above the transcript (Idea4Transcript.dc.html, "Idea 4 / Speakers"): the avatar and the name of a speaker.
/// A click opens the rename popover (<see cref="IsEditing"/>) with the box filled with the name and a hint with the number of lines the
/// new name will appear on. Enter (<see cref="CommitCommand"/>) saves, Esc (<see cref="CancelCommand"/>) closes without saving, and an empty
/// name (or the default word itself) brings back "You" / "Others".
/// </summary>
public sealed partial class SpeakerChipViewModel : ObservableObject
{
    private readonly Func<Speaker, string?, Task> _save;

    internal SpeakerChipViewModel(Speaker speaker, string? name, int lineCount, Func<Speaker, string?, Task> save)
    {
        Speaker = speaker;
        LineCount = lineCount;
        _save = save;
        Name = name;
        Label = SpeakerLabels.Of(speaker, name);
        Initial = SpeakerLabels.Initial(Label);
        EditText = Label;
        HintText = UiText.Format(lineCount == 1 ? Strings.Speakers_HintOne : Strings.Speakers_HintMany, lineCount);
    }

    public Speaker Speaker { get; }

    /// <summary>The avatar of the microphone track is filled with the ink, the other one with Stone.</summary>
    public bool IsMe => Speaker == Speaker.Me;

    /// <summary>How many lines of the recording this speaker has: the number in the hint.</summary>
    public int LineCount { get; }

    /// <summary>The name the user chose, or <c>null</c> for the default word.</summary>
    public string? Name { get; private set; }

    /// <summary>What the chip says: the name, or "You" / "Others".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    public partial string Label { get; private set; }

    /// <summary>"Rename Anna".</summary>
    public string ToolTip => UiText.Format(Strings.Speakers_ChipTip, Label);

    /// <summary>The letter in the avatar.</summary>
    [ObservableProperty]
    public partial string Initial { get; private set; }

    /// <summary>"Applies to 14 lines in this recording" (the singular for one line).</summary>
    public string HintText { get; }

    /// <summary>The name box limits the text to this many characters.</summary>
    public int MaxNameLength => SpeakerLabels.MaxNameLength;

    /// <summary>The popover is open.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    /// <summary>The text of the name box.</summary>
    [ObservableProperty]
    public partial string EditText { get; set; }

    /// <summary>Shows the new name on the chip (the store accepted it).</summary>
    internal void Apply(string? name)
    {
        Name = name;
        Label = SpeakerLabels.Of(Speaker, name);
        Initial = SpeakerLabels.Initial(Label);
        OnPropertyChanged(nameof(Name));
    }

    /// <summary>The click on the chip: the popover opens with the current name selected for typing.</summary>
    [RelayCommand]
    private void BeginEdit()
    {
        EditText = Label;
        IsEditing = true;
    }

    /// <summary>Enter: stores the name and closes the popover.</summary>
    [RelayCommand]
    private async Task CommitAsync()
    {
        if (!IsEditing)
        {
            return;
        }

        var text = EditText?.Trim() ?? string.Empty;
        IsEditing = false;

        // Nothing typed, or the default word itself: back to the default label.
        var name = text.Length == 0 || text == SpeakerLabels.Default(Speaker) ? null : text;
        await _save(Speaker, name).ConfigureAwait(true);
    }

    /// <summary>Esc, or a click outside: closes the popover and forgets what was typed.</summary>
    [RelayCommand]
    private void Cancel()
    {
        IsEditing = false;
        EditText = Label;
    }
}
