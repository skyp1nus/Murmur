namespace Murmur.Core.Sessions;

/// <summary>
/// The user's own words (#58): every alternative picked in "Did you mean" and every word kept as it was. Local to this PC and shared by
/// every recording. The fixes of uncertain words suggest from it first; biasing later transcriptions with it (sherpa-onnx hotwords) needs
/// beam search and is a follow-up (spike #53, D-052).
/// </summary>
public interface IVocabularyStore
{
    /// <summary>Most recently added first.</summary>
    ValueTask<IReadOnlyList<string>> GetVocabularyAsync(CancellationToken cancellationToken);

    /// <summary>Trimmed; duplicates (case-insensitive) move to the front instead of being added twice.</summary>
    ValueTask AddVocabularyWordAsync(string word, CancellationToken cancellationToken);
}
