using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests.Fakes;

/// <summary>An in-memory <see cref="IVocabularyStore"/> that orders and dedupes like the real one and records every add.</summary>
internal sealed class FakeVocabularyStore : IVocabularyStore
{
    private readonly List<string> _words = [];

    public FakeVocabularyStore(params string[] newestFirst) => _words.AddRange(newestFirst);

    public List<string> Adds { get; } = [];

    public IReadOnlyList<string> Words => _words;

    public Exception? GetFailure { get; set; }

    public Exception? AddFailure { get; set; }

    public ValueTask<IReadOnlyList<string>> GetVocabularyAsync(CancellationToken cancellationToken) =>
        GetFailure is { } failure ? throw failure : ValueTask.FromResult<IReadOnlyList<string>>([.. _words]);

    public ValueTask AddVocabularyWordAsync(string word, CancellationToken cancellationToken)
    {
        if (AddFailure is { } failure)
        {
            throw failure;
        }

        Adds.Add(word);
        var text = word.Trim();
        _words.RemoveAll(w => string.Equals(w, text, StringComparison.OrdinalIgnoreCase));
        _words.Insert(0, text);
        return ValueTask.CompletedTask;
    }
}

/// <summary>An <see cref="IWordSuggester"/> whose answer the test releases when it wants (the model is slow; the popover must not wait).</summary>
internal sealed class FakeWordSuggester : IWordSuggester
{
    private TaskCompletionSource<IReadOnlyList<string>> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<(int Segment, int Word)> Requests { get; } = [];

    /// <summary>The token of the last request (cancelled when the popover closes).</summary>
    public CancellationToken LastToken { get; private set; }

    /// <summary>Answer at once with these (no waiting).</summary>
    public IReadOnlyList<string>? Immediate { get; set; }

    public void Answer(params string[] suggestions) => _answer.TrySetResult(suggestions);

    public void Fail(Exception exception) => _answer.TrySetException(exception);

    public void Reset() => _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<IReadOnlyList<string>> SuggestAsync(Transcript transcript, int segmentIndex, int wordIndex, CancellationToken cancellationToken)
    {
        Requests.Add((segmentIndex, wordIndex));
        LastToken = cancellationToken;
        if (Immediate is { } now)
        {
            return now;
        }

        return await _answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
