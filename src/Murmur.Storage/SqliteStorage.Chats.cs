using System.Text.Json;
using Microsoft.Data.Sqlite;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.Storage;

public sealed partial class SqliteStorage
{
    /// <summary>Most recently updated first (ties: the newer thread first); a limit of zero or less yields an empty list.</summary>
    public ValueTask<IReadOnlyList<ChatThread>> ListThreadsAsync(int limit, CancellationToken cancellationToken) =>
        Run(() => ListThreads(limit), cancellationToken);

    public ValueTask<ChatThread> CreateThreadAsync(string title, SessionId? scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);
        return Run(() => CreateThread(title, scope), cancellationToken);
    }

    public ValueTask<IReadOnlyList<ChatMessage>> GetMessagesAsync(ChatThreadId thread, CancellationToken cancellationToken) =>
        Run(() => GetMessages(thread), cancellationToken);

    public ValueTask AppendMessageAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Text);
        ArgumentNullException.ThrowIfNull(message.Citations);
        return Run(() => AppendMessage(message), cancellationToken);
    }

    public ValueTask RenameThreadAsync(ChatThreadId thread, string title, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);
        return Run(() => RenameThread(thread, title), cancellationToken);
    }

    public ValueTask DeleteThreadAsync(ChatThreadId thread, CancellationToken cancellationToken) =>
        Run(() => DeleteThread(thread), cancellationToken);

    private IReadOnlyList<ChatThread> ListThreads(int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, title, scope_session, updated_at FROM chat_threads ORDER BY updated_at DESC, id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<ChatThread>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadThread(reader));
        }

        return result;
    }

    private ChatThread CreateThread(string title, SessionId? scope)
    {
        var id = ChatThreadId.New();
        var updated = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var connection = OpenConnection();
        if (scope is { } session && !SessionExists(connection, session))
        {
            throw new KeyNotFoundException($"Session {session} does not exist.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO chat_threads (id, title, scope_session, updated_at) VALUES ($id, $title, $scope, $updated)";
        command.Parameters.AddWithValue("$id", id.Value);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$scope", scope is { } s ? s.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("$updated", updated.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        return new ChatThread(id, title, scope, updated.ToLocalTime());
    }

    private static bool SessionExists(SqliteConnection connection, SessionId id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteScalar() is not null;
    }

    private IReadOnlyList<ChatMessage> GetMessages(ChatThreadId thread)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT idx, role, text, citations, at FROM chat_messages WHERE thread_id = $thread ORDER BY idx";
        command.Parameters.AddWithValue("$thread", thread.Value);
        var result = new List<ChatMessage>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ChatMessage(
                thread,
                reader.GetInt32(0),
                (TurnRole)reader.GetInt32(1),
                reader.GetString(2),
                DeserializeCitations(reader.GetString(3)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)).ToLocalTime()));
        }

        return result;
    }

    private void AppendMessage(ChatMessage message)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        long count;
        long lastRole = -1;
        using (var counting = connection.CreateCommand())
        {
            counting.Transaction = transaction;
            counting.CommandText =
                "SELECT (SELECT COUNT(*) FROM chat_messages WHERE thread_id = $thread), (SELECT COUNT(*) FROM chat_threads WHERE id = $thread), " +
                "(SELECT role FROM chat_messages WHERE thread_id = $thread ORDER BY idx DESC LIMIT 1)";
            counting.Parameters.AddWithValue("$thread", message.Thread.Value);
            using var reader = counting.ExecuteReader();
            reader.Read();
            if (reader.GetInt64(1) == 0)
            {
                throw new KeyNotFoundException($"Chat thread {message.Thread} does not exist.");
            }

            count = reader.GetInt64(0);
            lastRole = reader.IsDBNull(2) ? -1 : reader.GetInt64(2);
        }

        if (message.Index < 0 || message.Index > count)
        {
            throw new ArgumentOutOfRangeException(nameof(message), message.Index, $"The thread has {count} messages: a message goes at an index from 0 to {count}.");
        }

        if (message.Index < count)
        {
            // Only the last answer can be replaced; a question or an older message at a taken index means another writer got there first.
            if (message.Index != count - 1 || message.Role != TurnRole.Assistant || lastRole != (int)TurnRole.Assistant)
            {
                throw new InvalidOperationException($"Index {message.Index} of chat thread {message.Thread} is taken: only the last answer can be replaced.");
            }

            using var cut = connection.CreateCommand();
            cut.Transaction = transaction;
            cut.CommandText = "DELETE FROM chat_messages WHERE thread_id = $thread AND idx = $idx";
            cut.Parameters.AddWithValue("$thread", message.Thread.Value);
            cut.Parameters.AddWithValue("$idx", message.Index);
            cut.ExecuteNonQuery();
        }

        var at = message.At.ToUnixTimeMilliseconds();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO chat_messages (thread_id, idx, role, text, citations, at) VALUES ($thread, $idx, $role, $text, $citations, $at)";
            insert.Parameters.AddWithValue("$thread", message.Thread.Value);
            insert.Parameters.AddWithValue("$idx", message.Index);
            insert.Parameters.AddWithValue("$role", (int)message.Role);
            insert.Parameters.AddWithValue("$text", message.Text);
            insert.Parameters.AddWithValue("$citations", SerializeCitations(message.Citations));
            insert.Parameters.AddWithValue("$at", at);
            insert.ExecuteNonQuery();
        }

        using (var touch = connection.CreateCommand())
        {
            touch.Transaction = transaction;
            touch.CommandText = "UPDATE chat_threads SET updated_at = $at WHERE id = $thread";
            touch.Parameters.AddWithValue("$thread", message.Thread.Value);
            touch.Parameters.AddWithValue("$at", at);
            touch.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void RenameThread(ChatThreadId thread, string title)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE chat_threads SET title = $title WHERE id = $thread";
        command.Parameters.AddWithValue("$thread", thread.Value);
        command.Parameters.AddWithValue("$title", title);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new KeyNotFoundException($"Chat thread {thread} does not exist.");
        }
    }

    private void DeleteThread(ChatThreadId thread)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM chat_threads WHERE id = $thread";
        command.Parameters.AddWithValue("$thread", thread.Value);
        command.ExecuteNonQuery();
    }

    private static ChatThread ReadThread(SqliteDataReader reader) => new(
        new ChatThreadId(reader.GetString(0)),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : SessionId.Parse(reader.GetString(2)),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)).ToLocalTime());

    private static string SerializeCitations(IReadOnlyList<Citation> citations)
    {
        if (citations.Count == 0)
        {
            return "[]";
        }

        var dtos = new CitationDto[citations.Count];
        for (var i = 0; i < dtos.Length; i++)
        {
            var citation = citations[i];
            dtos[i] = new CitationDto(citation.Session.ToString(), (long)citation.At.TotalMilliseconds, citation.Label, citation.Snippet);
        }

        return JsonSerializer.Serialize(dtos, StorageJsonContext.Default.CitationDtoArray);
    }

    private static IReadOnlyList<Citation> DeserializeCitations(string json)
    {
        var dtos = JsonSerializer.Deserialize(json, StorageJsonContext.Default.CitationDtoArray);
        if (dtos is null || dtos.Length == 0)
        {
            return [];
        }

        var citations = new Citation[dtos.Length];
        for (var i = 0; i < citations.Length; i++)
        {
            var dto = dtos[i];
            citations[i] = new Citation(SessionId.Parse(dto.Session), TimeSpan.FromMilliseconds(dto.AtMs), dto.Label, dto.Snippet);
        }

        return citations;
    }
}
