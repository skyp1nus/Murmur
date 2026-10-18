using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.Core.Intelligence;

namespace Murmur.App.ViewModels;

/// <summary>
/// One chat bubble: the user's question (right, on Surface) or the assistant's answer (left), which streams in delta
/// by delta and ends with its timestamp chips.
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
    public partial string Text { get; private set; }

    /// <summary>True while the answer is still being generated.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowThinking))]
    public partial bool IsStreaming { get; private set; }

    /// <summary>Time chips under a finished answer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCitations))]
    public partial IReadOnlyList<CitationChipViewModel> Citations { get; private set; } = [];

    /// <summary>Why the answer stopped early (cancelled, AI paused, error), or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? StatusText { get; private set; }

    public bool HasText => Text.Length > 0;

    public bool HasCitations => Citations.Count > 0;

    public bool HasStatus => StatusText is not null;

    /// <summary>"Думаю…" while the answer is generating and nothing has arrived yet.</summary>
    public bool ShowThinking => IsStreaming && Text.Length == 0;

    /// <summary>The answer completed normally (it goes into the history of the next question).</summary>
    internal bool Succeeded { get; private set; }

    internal static ChatMessageViewModel User(string question) => new(TurnRole.User, question);

    internal static ChatMessageViewModel Answer() => new(TurnRole.Assistant, string.Empty);

    internal void Append(string delta)
    {
        if (_answer is null || delta.Length == 0)
        {
            return;
        }

        _answer.Append(delta);
        Text = _answer.ToString();
    }

    internal void SetCitations(IReadOnlyList<Citation> citations, Action<Citation> activate)
    {
        if (citations.Count == 0)
        {
            Citations = [];
            return;
        }

        // The resolver already orders and de-duplicates them; one chip per citation.
        var chips = new CitationChipViewModel[citations.Count];
        for (var i = 0; i < chips.Length; i++)
        {
            chips[i] = new CitationChipViewModel(citations[i], activate);
        }

        Citations = chips;
    }

    internal void Complete()
    {
        Succeeded = true;
        IsStreaming = false;
    }

    internal void Stop(string status)
    {
        StatusText = status;
        IsStreaming = false;
    }
}

/// <summary>A timestamp chip (Geist Mono, outline pill) under an answer; clicking it plays the recording from there.</summary>
public sealed class CitationChipViewModel
{
    private readonly Action<Citation> _activate;
    private ICommand? _activateCommand;

    internal CitationChipViewModel(Citation citation, Action<Citation> activate)
    {
        Citation = citation;
        _activate = activate;
        Label = string.IsNullOrWhiteSpace(citation.Label) ? UiFormat.Clock(citation.At) : citation.Label;
    }

    public Citation Citation { get; }

    /// <summary>The time, e.g. "12:34" (or "1:02:03" past the first hour).</summary>
    public string Label { get; }

    /// <summary>The transcript text at that moment (tooltip).</summary>
    public string Snippet => Citation.Snippet;

    public ICommand ActivateCommand => _activateCommand ??= new RelayCommand(() => _activate(Citation));
}
