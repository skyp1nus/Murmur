using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Transcription;

namespace Murmur.App.ViewModels;

/// <summary>
/// One transcript row: speaker, time chip and the text, plus which of its words is being spoken. Cheap to create
/// (a list holds hundreds): the text and the word offsets are built on first use, i.e. only for the rows that
/// are actually realised by the virtualising list.
/// </summary>
public sealed partial class SegmentViewModel : ObservableObject
{
    private static readonly int[] NoOffsets = [];

    private readonly TranscriptSegment _segment;
    private readonly Action<SegmentViewModel, int> _seek;
    private string? _text;
    private int[] _wordStarts = NoOffsets;
    private int[] _wordLengths = NoOffsets;
    private RelayCommand<int>? _seekWordCommand;
    private RelayCommand? _seekCommand;

    internal SegmentViewModel(TranscriptSegment segment, int index, int firstWord, Action<SegmentViewModel, int> seek)
    {
        _segment = segment;
        Index = index;
        FirstWord = firstWord;
        _seek = seek;
    }

    /// <summary>Position in the transcript.</summary>
    public int Index { get; }

    /// <summary>Number of the segment's first word in the whole transcript (words are numbered 0..n-1).</summary>
    internal int FirstWord { get; }

    public int WordCount => _segment.Words.Count;

    public TimeSpan Start => _segment.Start;

    public Speaker Speaker => _segment.Speaker;

    /// <summary>"You" for the microphone track, "Others" for the loopback track (the UI language).</summary>
    public string SpeakerLabel => _segment.Speaker == Speaker.Me ? Strings.Segment_Me : Strings.Segment_Others;

    /// <summary>The letter in the avatar: "Y" / "O" (the first letter of the label).</summary>
    public string Initial => SpeakerLabel.Length > 0 ? SpeakerLabel[..1].ToUpperInvariant() : string.Empty;

    /// <summary>The avatar of the microphone track is filled with the ink, the other one with Stone.</summary>
    public bool IsMe => _segment.Speaker == Speaker.Me;

    public TimeSpan End => _segment.End;

    /// <summary>Geist Mono time chip, e.g. "12:34".</summary>
    public string TimeText => UiFormat.Clock(_segment.Start);

    /// <summary>The segment's words joined by single spaces (or the segment text when it has no word timings).</summary>
    public string Text
    {
        get
        {
            EnsureText();
            return _text!;
        }
    }

    /// <summary>Start of every word inside <see cref="Text"/>; empty when the segment has no word timings.</summary>
    public int[] WordStarts
    {
        get
        {
            EnsureText();
            return _wordStarts;
        }
    }

    public int[] WordLengths
    {
        get
        {
            EnsureText();
            return _wordLengths;
        }
    }

    /// <summary>Index of the word being spoken inside this segment, or -1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleWord))]
    public partial int ActiveWord { get; set; } = -1;

    /// <summary>The word is marked in the list: only while the recording plays (a paused one shows the line, not a word, like the canvas).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleWord))]
    public partial bool ShowWord { get; set; }

    /// <summary>The word the list marks: <see cref="ActiveWord"/> while <see cref="ShowWord"/>, else -1.</summary>
    public int VisibleWord => ShowWord ? ActiveWord : -1;

    /// <summary>The player is in this segment: the row is filled.</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>The track of this speaker is switched off in the filters: the row is dimmed.</summary>
    [ObservableProperty]
    public partial bool IsDimmed { get; set; }

    /// <summary>The text the transcript search looks for (every occurrence in this row is marked), or <c>null</c>.</summary>
    [ObservableProperty]
    public partial string? HighlightTerm { get; set; }

    /// <summary>The segment's text contains <paramref name="term"/> (any case).</summary>
    internal bool Contains(string term) => Text.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parameter: index of the clicked word inside this segment. Seeks the player there and plays.</summary>
    public ICommand SeekWordCommand => _seekWordCommand ??= new RelayCommand<int>(word => _seek(this, word));

    /// <summary>The time chip: seeks to the start of the segment and plays.</summary>
    public ICommand SeekCommand => _seekCommand ??= new RelayCommand(() => _seek(this, 0));

    private void EnsureText()
    {
        if (_text is not null)
        {
            return;
        }

        var words = _segment.Words;
        if (words.Count == 0)
        {
            _text = _segment.Text;
            return;
        }

        var builder = new StringBuilder(_segment.Text.Length + words.Count);
        var starts = new int[words.Count];
        var lengths = new int[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i].Text.AsSpan().Trim();
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            starts[i] = builder.Length;
            lengths[i] = word.Length;
            builder.Append(word);
        }

        _wordStarts = starts;
        _wordLengths = lengths;
        _text = builder.ToString();
    }
}
