using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.App.Resources;
using Murmur.Core.Transcription;

namespace Murmur.App.ViewModels;

/// <summary>
/// Uncertain words (#58, Idea4Transcript.dc.html "Idea 4 / Low-confidence fix"). A word the recogniser doubted
/// (<see cref="TranscriptWord.IsUncertain"/>) is underlined in its line; a click on it (or Enter) opens "Did you mean" (<see cref="DidYouMean"/>)
/// under it. The alternatives come from the user's vocabulary at once and from the local model when it answers (it never holds the popover
/// up); picking one stores the fix (<see cref="ITranscriptStore.ReplaceWordAsync"/>: the line, the stored transcript and the search index)
/// and adds the word to the vocabulary; "Keep" stores the word as it is, which only takes its doubt away (in this recording).
/// </summary>
/// <remarks>
/// The search and the export read the fixed text at once; the AI's embedded chunks keep the old wording until the recording is indexed
/// again (not needed for the fix to show). The Session boards' demo words have no confidence, so the boards show no underline.
/// </remarks>
public sealed partial class SessionViewModel
{
    private CancellationTokenSource? _suggestionsCancellation;

    /// <summary>The open "Did you mean", or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDidYouMeanOpen))]
    public partial DidYouMeanViewModel? DidYouMean { get; private set; }

    /// <summary>The popover is open; the view's light dismiss (a click elsewhere) sets it to <c>false</c>, which closes it.</summary>
    public bool IsDidYouMeanOpen
    {
        get => DidYouMean is not null;
        set
        {
            if (!value)
            {
                CloseDidYouMean();
            }
        }
    }

    /// <summary>The transcript as it is now, the user's fixes included (for tests).</summary>
    internal Transcript? CurrentTranscript => _transcript;

    /// <summary>The suggestions of the open popover, both sources (for tests).</summary>
    internal Task LastSuggestions { get; private set; } = Task.CompletedTask;

    /// <summary>Opens "Did you mean" for an uncertain word of a row (the row's <see cref="SegmentViewModel.OpenWordCommand"/>).</summary>
    internal void OpenDidYouMean(SegmentViewModel row, int word)
    {
        if (_disposed || !row.IsUncertain(word))
        {
            return;
        }

        CloseDidYouMean();
        var popover = new DidYouMeanViewModel(row, word, FixWordAsync, CloseDidYouMean);
        var cancellation = _suggestionsCancellation = new CancellationTokenSource();
        DidYouMean = popover;
        LastSuggestions = LoadSuggestionsAsync(popover, cancellation.Token);
    }

    /// <summary>Closes the popover (if open) and stops asking the model.</summary>
    internal void CloseDidYouMean()
    {
        _suggestionsCancellation?.Cancel();
        _suggestionsCancellation?.Dispose();
        _suggestionsCancellation = null;
        DidYouMean = null;
    }

    /// <summary>The vocabulary first (it is local and quick), then the model's ideas behind them; a source that fails is left out.</summary>
    private async Task LoadSuggestionsAsync(DidYouMeanViewModel popover, CancellationToken cancellationToken)
    {
        // The model is asked at once, in parallel with the vocabulary: it is the slow one.
        var model = AskModelAsync(popover, cancellationToken);

        IReadOnlyList<string> vocabulary = [];
        if (_services.Vocabulary is { } store)
        {
            try
            {
                vocabulary = WordSuggestions.FromVocabulary(popover.Word, await store.GetVocabularyAsync(cancellationToken).ConfigureAwait(true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A vocabulary that cannot be read only means fewer chips.
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!ReferenceEquals(DidYouMean, popover))
        {
            return;
        }

        popover.SetAlternatives(WordSuggestions.Merge(popover.Word, vocabulary, []));

        IReadOnlyList<string> ideas;
        try
        {
            ideas = await model.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ideas.Count > 0 && ReferenceEquals(DidYouMean, popover))
        {
            popover.SetAlternatives(WordSuggestions.Merge(popover.Word, vocabulary, ideas));
        }
    }

    private async Task<IReadOnlyList<string>> AskModelAsync(DidYouMeanViewModel popover, CancellationToken cancellationToken)
    {
        if (_services.WordSuggester is not { } suggester || _transcript is not { } transcript)
        {
            return [];
        }

        try
        {
            return await suggester.SuggestAsync(transcript, popover.Row.Index, popover.WordIndex, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The model failed to start or answered nonsense: the vocabulary's chips and "Keep" are still there.
            return [];
        }
    }

    /// <summary>
    /// Stores the choice and shows it: the word in the stored transcript and the search index, then the line (the underline goes), the
    /// export, the search hits; then the word goes to the vocabulary. A store that fails changes nothing and says so in <see cref="ActionMessage"/>.
    /// </summary>
    private async Task FixWordAsync(DidYouMeanViewModel popover, WordChoiceViewModel choice)
    {
        var row = popover.Row;
        var replacement = popover.ReplacementFor(choice);
        try
        {
            await _transcripts.ReplaceWordAsync(Id, row.Index, popover.WordIndex, replacement, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The store is SQLite: its errors (a locked file, a full disk) must not take the app down.
            if (ReferenceEquals(DidYouMean, popover))
            {
                CloseDidYouMean();
            }

            ActionMessage = UiText.Format(Strings.DidYouMean_FixFailed, ex.Message);
            return;
        }

        if (_disposed)
        {
            return;
        }

        var segment = row.Segment;
        row.ApplyFix(popover.WordIndex, replacement, WordSuggestions.ReplaceInText(segment.Text, segment.Words, popover.WordIndex, replacement));
        if (_transcript is { } transcript && row.Index < transcript.Segments.Count)
        {
            var segments = new TranscriptSegment[transcript.Segments.Count];
            for (var i = 0; i < segments.Length; i++)
            {
                segments[i] = i == row.Index ? row.Segment : transcript.Segments[i];
            }

            _transcript = transcript with { Segments = segments };
        }

        if (ReferenceEquals(DidYouMean, popover))
        {
            CloseDidYouMean();
        }

        RefreshExport();
        if (HighlightTerm is not null)
        {
            ApplySearch();
        }

        if (_services.Vocabulary is { } vocabulary)
        {
            try
            {
                await vocabulary.AddVocabularyWordAsync(choice.Value, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The fix is stored; a vocabulary that cannot be written only means the word is not offered next time.
            }
        }
    }
}
