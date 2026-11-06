using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;

namespace Murmur.App.ViewModels;

/// <summary>
/// "Did you mean" for one uncertain word (#58, Idea4Transcript.dc.html "Idea 4 / Low-confidence fix"): the label, a chip per alternative
/// (the first, most likely one filled, the others outlined) and, last, "Keep &lt;word&gt;". The alternatives arrive while the popover is open
/// (<see cref="SetAlternatives"/>): the vocabulary at once, the model's when it has answered; the Keep chip is there from the start.
/// </summary>
public sealed partial class DidYouMeanViewModel : ObservableObject
{
    private readonly Func<DidYouMeanViewModel, WordChoiceViewModel, Task> _pick;
    private readonly Action _close;
    private RelayCommand? _closeCommand;

    internal DidYouMeanViewModel(SegmentViewModel row, int word, Func<DidYouMeanViewModel, WordChoiceViewModel, Task> pick, Action close)
    {
        Row = row;
        WordIndex = word;
        _pick = pick;
        _close = close;
        Original = row.Segment.Words[word].Text.Trim();
        (Prefix, Word, Suffix) = WordSuggestions.Split(Original);
        Name = UiText.Format(Strings.DidYouMean_Name, Word);
        Choices = [new WordChoiceViewModel(this, Word, UiText.Format(Strings.DidYouMean_Keep, Word), isKeep: true, isPrimary: false)];
    }

    /// <summary>The row of the word.</summary>
    internal SegmentViewModel Row { get; }

    /// <summary>Index of the word in its row.</summary>
    internal int WordIndex { get; }

    /// <summary>The word as the transcript has it, punctuation included ("parquet?").</summary>
    internal string Original { get; }

    /// <summary>The word without the punctuation around it: what the chips replace and what "Keep" names.</summary>
    public string Word { get; }

    /// <summary>The punctuation in front of the word, kept by a fix.</summary>
    internal string Prefix { get; }

    /// <summary>The punctuation after the word, kept by a fix.</summary>
    internal string Suffix { get; }

    /// <summary>"Did you mean".</summary>
    public string Title => Strings.DidYouMean_Title;

    /// <summary>The accessible name of the popover: "Suggestions for “parquet”".</summary>
    public string Name { get; }

    /// <summary>The alternatives in order, then "Keep".</summary>
    public ObservableCollection<WordChoiceViewModel> Choices { get; }

    /// <summary>The alternatives on offer (without "Keep"), for tests.</summary>
    internal IReadOnlyList<string> Alternatives => [.. Choices.Where(c => !c.IsKeep).Select(c => c.Value)];

    /// <summary>A fix is being stored: the chips do nothing until it is done.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Esc: closes without changing anything.</summary>
    public ICommand CloseCommand => _closeCommand ??= new RelayCommand(_close);

    /// <summary>Shows these alternatives (at most <see cref="WordSuggestions.MaxAlternatives"/>, already merged) in front of "Keep"; chips that stay keep their place.</summary>
    internal void SetAlternatives(IReadOnlyList<string> alternatives)
    {
        var keep = Choices[^1];
        for (var i = 0; i < alternatives.Count; i++)
        {
            var primary = i == 0;
            if (i < Choices.Count - 1 && string.Equals(Choices[i].Value, alternatives[i], StringComparison.Ordinal))
            {
                Choices[i].IsPrimary = primary;
                continue;
            }

            var choice = new WordChoiceViewModel(this, alternatives[i], alternatives[i], isKeep: false, isPrimary: primary);
            if (i < Choices.Count - 1)
            {
                Choices[i] = choice;
            }
            else
            {
                Choices.Insert(Choices.Count - 1, choice);
            }
        }

        while (Choices.Count - 1 > alternatives.Count)
        {
            Choices.RemoveAt(Choices.Count - 2);
        }

        System.Diagnostics.Debug.Assert(ReferenceEquals(Choices[^1], keep), "Keep is always the last chip.");
    }

    /// <summary>What a choice writes into the transcript: the alternative with the punctuation of the word kept, or the word as it was.</summary>
    internal string ReplacementFor(WordChoiceViewModel choice) => choice.IsKeep ? Original : Prefix + choice.Value + Suffix;

    internal async Task PickAsync(WordChoiceViewModel choice)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _pick(this, choice).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>One chip of "Did you mean": an alternative (or "Keep &lt;word&gt;"); a click or Enter picks it.</summary>
public sealed partial class WordChoiceViewModel : ObservableObject
{
    private readonly DidYouMeanViewModel _owner;
    private AsyncRelayCommand? _pickCommand;

    internal WordChoiceViewModel(DidYouMeanViewModel owner, string value, string text, bool isKeep, bool isPrimary)
    {
        _owner = owner;
        Value = value;
        Text = text;
        IsKeep = isKeep;
        IsPrimary = isPrimary;
    }

    /// <summary>The word the choice puts in the transcript (and the vocabulary).</summary>
    public string Value { get; }

    /// <summary>What the chip says.</summary>
    public string Text { get; }

    /// <summary>"Keep &lt;word&gt;": confirms the word as it is.</summary>
    public bool IsKeep { get; }

    /// <summary>The most likely alternative: the filled chip.</summary>
    [ObservableProperty]
    public partial bool IsPrimary { get; internal set; }

    public ICommand PickCommand => _pickCommand ??= new AsyncRelayCommand(() => _owner.PickAsync(this));
}
