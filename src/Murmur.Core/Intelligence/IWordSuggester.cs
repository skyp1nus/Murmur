using Murmur.Core.Transcription;

namespace Murmur.Core.Intelligence;

/// <summary>
/// Replacements for an uncertain word of a transcript (#58), asked of the local chat model with the sentence around the word. Like every
/// use of the models it never runs while a recording is in progress, and the model loads lazily in the worker.
/// </summary>
public interface IWordSuggester
{
    /// <summary>Up to 3 replacements for the word at the given position, most likely first; empty when the model is not available.</summary>
    ValueTask<IReadOnlyList<string>> SuggestAsync(Transcript transcript, int segmentIndex, int wordIndex, CancellationToken cancellationToken);
}
