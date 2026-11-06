using Murmur.Core.Sessions;

namespace Murmur.Storage;

public sealed partial class SqliteStorage
{
    /// <summary>The longest vocabulary word that is stored; a longer one is cut.</summary>
    public const int MaxVocabularyWordLength = 100;

    /// <summary>Every word, the most recently added first.</summary>
    public ValueTask<IReadOnlyList<string>> GetVocabularyAsync(CancellationToken cancellationToken) =>
        Run<IReadOnlyList<string>>(GetVocabulary, cancellationToken);

    /// <summary>
    /// Adds a word (trimmed, cut to <see cref="MaxVocabularyWordLength"/> characters) at the front. A word that is already there in any
    /// case moves to the front and takes the new spelling. A blank word throws <see cref="ArgumentException"/>.
    /// </summary>
    public ValueTask AddVocabularyWordAsync(string word, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(word);
        var text = word.Trim();
        if (text.Length > MaxVocabularyWordLength)
        {
            // Never end on half of a surrogate pair.
            var length = char.IsHighSurrogate(text[MaxVocabularyWordLength - 1]) ? MaxVocabularyWordLength - 1 : MaxVocabularyWordLength;
            text = text[..length].TrimEnd();
        }

        return Run(() => AddVocabularyWord(text), cancellationToken);
    }

    private List<string> GetVocabulary()
    {
        var words = new List<string>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT word FROM vocabulary ORDER BY seq DESC";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            words.Add(reader.GetString(0));
        }

        return words;
    }

    private void AddVocabularyWord(string word)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        // One statement: the next seq is read and written atomically, so two adds never share a place.
        command.CommandText =
            "INSERT INTO vocabulary (key, word, seq) VALUES ($key, $word, (SELECT COALESCE(MAX(seq), 0) + 1 FROM vocabulary)) " +
            "ON CONFLICT (key) DO UPDATE SET word = excluded.word, seq = excluded.seq";
        command.Parameters.AddWithValue("$key", word.ToLowerInvariant());
        command.Parameters.AddWithValue("$word", word);
        command.ExecuteNonQuery();
    }
}
