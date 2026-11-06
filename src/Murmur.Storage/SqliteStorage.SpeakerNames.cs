using System.Text;
using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Storage;

public sealed partial class SqliteStorage
{
    /// <summary>The longest speaker name that is stored; a longer one is cut.</summary>
    public const int MaxSpeakerNameLength = 40;

    /// <summary>The names of the two speakers; <see cref="SpeakerNames.Default"/> when none was stored.</summary>
    public ValueTask<SpeakerNames> GetSpeakerNamesAsync(SessionId session, CancellationToken cancellationToken) =>
        Run(() => GetSpeakerNames(session), cancellationToken);

    /// <summary>
    /// Stores the name of one speaker: line breaks become spaces, the text is trimmed and cut to <see cref="MaxSpeakerNameLength"/>
    /// characters; an empty result removes the name (back to the default label). A session that does not exist throws <see cref="KeyNotFoundException"/>.
    /// </summary>
    public ValueTask SetSpeakerNameAsync(SessionId session, Speaker speaker, string? name, CancellationToken cancellationToken) =>
        Run(() => SetSpeakerName(session, speaker, NormalizeSpeakerName(name)), cancellationToken);

    /// <summary>Trims, replaces line breaks with spaces and cuts to <see cref="MaxSpeakerNameLength"/>; <c>null</c> when nothing is left.</summary>
    internal static string? NormalizeSpeakerName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(IsLineBreak(c) ? ' ' : c);
        }

        var text = builder.ToString().Trim();
        if (text.Length > MaxSpeakerNameLength)
        {
            // Never end on half of a surrogate pair.
            var length = char.IsHighSurrogate(text[MaxSpeakerNameLength - 1]) ? MaxSpeakerNameLength - 1 : MaxSpeakerNameLength;
            text = text[..length].TrimEnd();
        }

        return text.Length == 0 ? null : text;
    }

    private static bool IsLineBreak(char c) =>
        c is '\r' or '\n' or '\u0085' || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator;

    private SpeakerNames GetSpeakerNames(SessionId session)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT me, others FROM speaker_names WHERE session_id = $id";
        command.Parameters.AddWithValue("$id", session.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new SpeakerNames(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))
            : SpeakerNames.Default;
    }

    private void SetSpeakerName(SessionId session, Speaker speaker, string? name)
    {
        using var connection = OpenConnection();

        // The check and the write are one transaction (like SaveChapters): a session deleted in between is a KeyNotFoundException.
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM sessions WHERE id = $id";
            exists.Parameters.AddWithValue("$id", session.ToString());
            if (exists.ExecuteScalar() is null)
            {
                throw new KeyNotFoundException($"Session {session} does not exist.");
            }
        }

        var column = speaker == Speaker.Me ? "me" : "others";
        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText =
                $"INSERT INTO speaker_names (session_id, {column}) VALUES ($id, $name) " +
                $"ON CONFLICT (session_id) DO UPDATE SET {column} = excluded.{column}";
            upsert.Parameters.AddWithValue("$id", session.ToString());
            upsert.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
            upsert.ExecuteNonQuery();
        }

        // A row with both names back to the default carries nothing: remove it.
        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM speaker_names WHERE session_id = $id AND me IS NULL AND others IS NULL";
            prune.Parameters.AddWithValue("$id", session.ToString());
            prune.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
