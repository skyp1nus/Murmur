using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Murmur.Core.Transcription;

namespace Murmur.App.ViewModels;

/// <summary>
/// The uncertain words of a row (#58, Idea4Transcript.dc.html "Idea 4 / Low-confidence fix"): which words the recogniser doubted
/// (<see cref="TranscriptWord.IsUncertain"/>), the command a click on one runs, and the fix that puts the user's choice in the row.
/// </summary>
public sealed partial class SegmentViewModel
{
    private bool[]? _uncertain;
    private bool _uncertainKnown;
    private RelayCommand<int>? _openWordCommand;

    /// <summary>Opens "Did you mean" for a word of this row (set by the session); <c>null</c> = an uncertain word seeks like any other.</summary>
    internal Action<SegmentViewModel, int>? OpenWord { get; init; }

    /// <summary>The segment as it is now (with the user's fixes).</summary>
    internal TranscriptSegment Segment => _segment;

    /// <summary>Per word: the recogniser doubted it. <c>null</c> when no word of the row is uncertain (most rows; nothing is allocated for them).</summary>
    public bool[]? UncertainWords
    {
        get
        {
            if (!_uncertainKnown)
            {
                _uncertain = Flags(_segment.Words);
                _uncertainKnown = true;
            }

            return _uncertain;
        }
    }

    /// <summary>Parameter: index of the uncertain word that was clicked. Opens "Did you mean" for it.</summary>
    public ICommand OpenWordCommand => _openWordCommand ??= new RelayCommand<int>(
        word => OpenWord?.Invoke(this, word),
        word => OpenWord is not null && IsUncertain(word));

    /// <summary>The word is in the row and the recogniser doubted it.</summary>
    internal bool IsUncertain(int word) => UncertainWords is { } flags && word >= 0 && word < flags.Length && flags[word];

    /// <summary>
    /// Puts the stored fix in the row: the word gets <paramref name="text"/> and loses its doubt (no underline), the text of the row is
    /// rebuilt the way the store rebuilt it. The timings, and so the playback and the marks, do not change.
    /// </summary>
    internal void ApplyFix(int word, string text, string segmentText)
    {
        var words = new TranscriptWord[_segment.Words.Count];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = _segment.Words[i];
        }

        words[word] = words[word] with { Text = text, Confidence = null };
        _segment = _segment with { Words = words, Text = segmentText };
        _text = null;
        _uncertainKnown = false;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(WordStarts));
        OnPropertyChanged(nameof(WordLengths));
        OnPropertyChanged(nameof(UncertainWords));
        _openWordCommand?.NotifyCanExecuteChanged();
    }

    private static bool[]? Flags(IReadOnlyList<TranscriptWord> words)
    {
        bool[]? flags = null;
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i].IsUncertain)
            {
                (flags ??= new bool[words.Count])[i] = true;
            }
        }

        return flags;
    }
}
