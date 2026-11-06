using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Storage;

public sealed partial class SqliteStorage
{
    /// <summary>
    /// Replaces the text of one word (trimmed) and sets its confidence to unknown, rewrites the segment's text with the new word in the same
    /// place, and through the update trigger its FTS5 row, in one transaction. The summary, the chapters, the embedded chunks and every other
    /// table are left alone (the chunks keep the old wording until the session is indexed again). An unknown session or an index out of range
    /// throws <see cref="ArgumentOutOfRangeException"/>, a blank text <see cref="ArgumentException"/>.
    /// </summary>
    public ValueTask ReplaceWordAsync(SessionId session, int segmentIndex, int wordIndex, string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfNegative(segmentIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(wordIndex);
        var replacement = text.Trim();
        return Run(() => ReplaceWord(session, segmentIndex, wordIndex, replacement), cancellationToken);
    }

    /// <summary>
    /// <paramref name="text"/> with word <paramref name="index"/> of <paramref name="words"/> replaced: the words are found in the text one after
    /// the other, so the spacing and anything the text has besides the words stay as they were. When a word cannot be found (a text that does
    /// not spell its words) the text is rebuilt from the new words joined by spaces.
    /// </summary>
    internal static string ReplaceWordInText(string text, IReadOnlyList<TranscriptWord> words, int index, string replacement)
    {
        var at = 0;
        for (var i = 0; i <= index; i++)
        {
            var word = words[i].Text.AsSpan().Trim();
            if (word.IsEmpty)
            {
                if (i == index)
                {
                    break;
                }

                continue;
            }

            var found = text.AsSpan(at).IndexOf(word, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            found += at;
            if (i == index)
            {
                return string.Concat(text.AsSpan(0, found), replacement, text.AsSpan(found + word.Length));
            }

            at = found + word.Length;
        }

        var joined = new System.Text.StringBuilder(text.Length + replacement.Length);
        for (var i = 0; i < words.Count; i++)
        {
            var word = i == index ? replacement.AsSpan() : words[i].Text.AsSpan().Trim();
            if (word.IsEmpty)
            {
                continue;
            }

            if (joined.Length > 0)
            {
                joined.Append(' ');
            }

            joined.Append(word);
        }

        return joined.ToString();
    }

    private void ReplaceWord(SessionId session, int segmentIndex, int wordIndex, string replacement)
    {
        var id = session.ToString();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        long rowId;
        string text;
        TranscriptWord[] words;
        using (var select = connection.CreateCommand())
        {
            // The segment at that position of GetTranscriptAsync's order.
            select.Transaction = transaction;
            select.CommandText = "SELECT id, text, words FROM segments WHERE session_id = $id ORDER BY start_ms, idx LIMIT 1 OFFSET $offset";
            select.Parameters.AddWithValue("$id", id);
            select.Parameters.AddWithValue("$offset", segmentIndex);
            using var reader = select.ExecuteReader();
            if (!reader.Read())
            {
                throw new ArgumentOutOfRangeException(nameof(segmentIndex), segmentIndex, $"Session {session} has no transcript segment {segmentIndex}.");
            }

            rowId = reader.GetInt64(0);
            text = reader.GetString(1);
            words = reader.IsDBNull(2) ? [] : WordsCodec.Decode(reader.GetFieldValue<byte[]>(2));
        }

        if (wordIndex >= words.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(wordIndex), wordIndex, $"Segment {segmentIndex} of session {session} has {words.Length} words.");
        }

        var newText = ReplaceWordInText(text, words, wordIndex, replacement);
        words[wordIndex] = words[wordIndex] with { Text = replacement, Confidence = null };

        using (var update = connection.CreateCommand())
        {
            // The AFTER UPDATE OF text trigger replaces the FTS5 row.
            update.Transaction = transaction;
            update.CommandText = "UPDATE segments SET text = $text, words = $words WHERE id = $row";
            update.Parameters.AddWithValue("$text", newText);
            update.Parameters.Add("$words", SqliteType.Blob).Value = WordsCodec.Encode(words);
            update.Parameters.AddWithValue("$row", rowId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
