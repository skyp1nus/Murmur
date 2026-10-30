using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>
/// One chat bubble: the user's question (right, on Surface) or the assistant's answer (left), which streams in delta
/// by delta and ends with its timestamp chips, the Sources cards and the Copy / Regenerate buttons.
/// </summary>
public sealed partial class ChatMessageViewModel : ObservableObject
{
    private readonly StringBuilder? _answer;

    private ChatMessageViewModel(TurnRole role, string text)
    {
        Role = role;
        Text = text;
        if (role == TurnRole.Assistant)
        {
            _answer = new StringBuilder();
            IsStreaming = true;
        }
    }

    public TurnRole Role { get; }

    public bool IsUser => Role == TurnRole.User;

    public bool IsAssistant => Role == TurnRole.Assistant;

    /// <summary>The question, or the answer received so far.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowThinking))]
    [NotifyPropertyChangedFor(nameof(HasText))]
    [NotifyPropertyChangedFor(nameof(ShowStreamingText))]
    [NotifyPropertyChangedFor(nameof(ShowCopy))]
    [NotifyPropertyChangedFor(nameof(HasActions))]
    public partial string Text { get; private set; }

    /// <summary>True while the answer is still being generated.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowThinking))]
    [NotifyPropertyChangedFor(nameof(ShowStreamingText))]
    [NotifyPropertyChangedFor(nameof(ShowParagraphs))]
    [NotifyPropertyChangedFor(nameof(ShowCopy))]
    [NotifyPropertyChangedFor(nameof(ShowRegenerate))]
    [NotifyPropertyChangedFor(nameof(HasActions))]
    public partial bool IsStreaming { get; private set; }

    /// <summary>One time chip per citation of a finished answer (the list the per-recording chat shows under the answer).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCitations))]
    public partial IReadOnlyList<CitationChipViewModel> Citations { get; private set; } = [];

    /// <summary>The finished answer as paragraphs with the time chips inline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowParagraphs))]
    public partial IReadOnlyList<AnswerParagraph> Paragraphs { get; private set; } = [];

    /// <summary>The "Sources" cards under a finished answer: the recording, the moment and the quote of every citation.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SourceCardViewModel> Sources { get; private set; } = [];

    /// <summary>Why the answer stopped early (cancelled, AI paused, error), or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? StatusText { get; private set; }

    /// <summary>"Copied" or why copying failed, shown under the answer for a moment; <c>null</c> otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCopyStatus))]
    public partial string? CopyStatus { get; internal set; }

    /// <summary>Why the exchange is not saved (the store failed, or the chat's recording was deleted); <c>null</c> when it is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveNote))]
    public partial string? SaveNote { get; internal set; }

    public bool HasCopyStatus => CopyStatus is not null;

    public bool HasSaveNote => SaveNote is not null;

    /// <summary>This is the last message of the chat (the only answer that can be regenerated).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRegenerate))]
    [NotifyPropertyChangedFor(nameof(HasActions))]
    public partial bool IsLastAnswer { get; internal set; }

    /// <summary>Copies the answer's text (set by the chat that owns the message).</summary>
    public ICommand? CopyCommand { get; internal set; }

    /// <summary>Asks the last question again and replaces this answer (set by the chat that owns the message).</summary>
    public ICommand? RegenerateCommand { get; internal set; }

    public bool HasText => Text.Length > 0;

    public bool HasCitations => Citations.Count > 0;

    /// <summary>
    /// The Sources cards show when the answer cites more than one moment, or a moment that has no time chip in its text (the resolver cites
    /// the best excerpt when the model wrote no marker): with a single cited moment that is already a chip in the line the chip is the whole
    /// source (the canvas draws the second answer that way).
    /// </summary>
    public bool HasSources { get; private set; }

    public bool HasStatus => StatusText is not null;

    /// <summary>"Думаю…" while the answer is generating and nothing has arrived yet.</summary>
    public bool ShowThinking => IsStreaming && Text.Length == 0;

    /// <summary>The raw text while it streams (the chips come when the answer is complete).</summary>
    public bool ShowStreamingText => IsStreaming && Text.Length > 0;

    /// <summary>The finished answer: paragraphs with inline chips.</summary>
    public bool ShowParagraphs => !IsStreaming && Paragraphs.Count > 0;

    public bool ShowCopy => IsAssistant && !IsStreaming && Text.Length > 0;

    public bool ShowRegenerate => IsAssistant && !IsStreaming && IsLastAnswer;

    /// <summary>The icon buttons under the answer.</summary>
    public bool HasActions => ShowCopy || ShowRegenerate;

    /// <summary>The answer completed normally (it goes into the history of the next question).</summary>
    internal bool Succeeded { get; private set; }

    /// <summary>The index of this message in the saved thread; <c>null</c> while it is not saved.</summary>
    internal int? StoredIndex { get; set; }

    /// <summary>The citations of the answer as it was saved (empty for a question or an answer without any).</summary>
    internal IReadOnlyList<Citation> CitationData { get; private set; } = [];

    internal static ChatMessageViewModel User(string question) => new(TurnRole.User, question);

    internal static ChatMessageViewModel Answer() => new(TurnRole.Assistant, string.Empty);

    /// <summary>A message read back from a saved thread: a finished answer (with its chips and sources) or a question.</summary>
    internal static ChatMessageViewModel Restore(ChatMessage message, Action<Citation> activate, IReadOnlyDictionary<SessionId, string?>? titles)
    {
        var restored = new ChatMessageViewModel(message.Role, message.Text) { StoredIndex = message.Index };
        if (message.Role == TurnRole.Assistant)
        {
            restored._answer!.Append(message.Text);
            restored.SetCitations(message.Citations, activate, titles);
            restored.Complete();
        }

        return restored;
    }

    internal void Append(string delta)
    {
        if (_answer is null || delta.Length == 0)
        {
            return;
        }

        _answer.Append(delta);
        Text = _answer.ToString();
    }

    internal void SetCitations(IReadOnlyList<Citation> citations, Action<Citation> activate, IReadOnlyDictionary<SessionId, string?>? titles = null)
    {
        CitationData = citations;
        if (citations.Count == 0)
        {
            Citations = [];
            Sources = [];
            UpdateSources();
            return;
        }

        // The resolver already orders and de-duplicates them; one chip and one card per citation.
        var primary = citations[0].Session;
        var chips = new CitationChipViewModel[citations.Count];
        var cards = new SourceCardViewModel[citations.Count];
        for (var i = 0; i < chips.Length; i++)
        {
            var citation = citations[i];
            string? title = null;
            titles?.TryGetValue(citation.Session, out title);
            var isLink = titles is null || title is not null;
            chips[i] = new CitationChipViewModel(citation, activate, ChipLabel(citation, title, citation.Session != primary), isLink);
            cards[i] = new SourceCardViewModel(
                citation,
                activate,
                titles is null ? string.Empty : title is null ? Strings.Chat_SourceDeleted : string.IsNullOrWhiteSpace(title) ? Strings.Common_UntitledRecording : title,
                isLink);
        }

        Citations = chips;
        Sources = cards;
    }

    internal void Complete()
    {
        Succeeded = true;
        IsStreaming = false;
        Paragraphs = BuildParagraphs();
        UpdateSources();
    }

    internal void Stop(string status)
    {
        StatusText = status;
        IsStreaming = false;
        Paragraphs = BuildParagraphs();
        UpdateSources();
    }

    /// <summary>"12:34", or "Title · 12:34" for a moment of another recording than the answer's first source.</summary>
    private static string ChipLabel(Citation citation, string? title, bool otherSession)
    {
        var label = string.IsNullOrWhiteSpace(citation.Label) ? UiFormat.Clock(citation.At) : citation.Label;
        if (!otherSession || string.IsNullOrWhiteSpace(title) || label != UiFormat.Clock(citation.At))
        {
            return label;
        }

        var shortTitle = title.Length > MaxChipTitle ? title[..(MaxChipTitle - 1)] + "…" : title;
        return shortTitle + " · " + label;
    }

    private const int MaxChipTitle = 24;

    private IReadOnlyList<AnswerParagraph> BuildParagraphs()
    {
        if (Text.Length == 0)
        {
            return [];
        }

        var chips = Citations;
        _inline.Clear();
        return AnswerText.Parse(Text, at =>
        {
            foreach (var chip in chips)
            {
                if (chip.Citation.At == at)
                {
                    _inline.Add(chip);
                    return chip;
                }
            }

            return null;
        });
    }

    private readonly HashSet<CitationChipViewModel> _inline = [];

    private void UpdateSources()
    {
        var visible = Sources.Count > 1 || (Sources.Count == 1 && Citations.Any(c => !_inline.Contains(c)));
        if (visible != HasSources)
        {
            HasSources = visible;
            OnPropertyChanged(nameof(HasSources));
        }
    }
}

/// <summary>A timestamp chip (Geist Mono, Surface pill) in or under an answer; clicking it plays the recording from there.</summary>
public sealed class CitationChipViewModel
{
    private readonly Action<Citation> _activate;
    private ICommand? _activateCommand;

    internal CitationChipViewModel(Citation citation, Action<Citation> activate, string? label = null, bool isLink = true)
    {
        Citation = citation;
        _activate = activate;
        IsLink = isLink;
        Label = label ?? (string.IsNullOrWhiteSpace(citation.Label) ? UiFormat.Clock(citation.At) : citation.Label);
    }

    public Citation Citation { get; }

    /// <summary>The time, e.g. "12:34" (or "1:02:03" past the first hour); "Retro · 31:05" for a moment of another recording.</summary>
    public string Label { get; }

    /// <summary>The transcript text at that moment (tooltip).</summary>
    public string Snippet => Citation.Snippet;

    /// <summary>The cited recording still exists: the chip opens it. A chip of a deleted recording stays as text.</summary>
    public bool IsLink { get; }

    /// <summary>The tooltip: the quote, or that the recording was deleted (the chip looks the same either way).</summary>
    public string Tooltip => IsLink ? Citation.Snippet : Strings.Chat_SourceDeleted;

    public ICommand ActivateCommand => _activateCommand ??= new RelayCommand(() => _activate(Citation), () => IsLink);
}

/// <summary>A card of the "Sources" grid under an answer: the recording's title, the moment and the quoted snippet.</summary>
public sealed class SourceCardViewModel
{
    private readonly Action<Citation> _activate;
    private ICommand? _openCommand;

    internal SourceCardViewModel(Citation citation, Action<Citation> activate, string title, bool isLink)
    {
        Citation = citation;
        _activate = activate;
        Title = title;
        IsLink = isLink;
    }

    public Citation Citation { get; }

    public string Title { get; }

    public string Time => UiFormat.Clock(Citation.At);

    public string Snippet => Citation.Snippet;

    /// <summary>The recording still exists: the card opens it at <see cref="Time"/>.</summary>
    public bool IsLink { get; }

    /// <summary>Empty while the card is a link; says that the recording was deleted otherwise.</summary>
    public string? Tooltip => IsLink ? null : Strings.Chat_SourceDeleted;

    public ICommand OpenCommand => _openCommand ??= new RelayCommand(() => _activate(Citation), () => IsLink);
}
