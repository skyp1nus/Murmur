using System.Text.Json;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.Storage;

public sealed partial class SqliteStorage
{
    /// <summary>The stored summary of the session, or <c>null</c> when none was saved. Times come back with millisecond precision.</summary>
    public ValueTask<SessionSummary?> GetSummaryAsync(SessionId session, CancellationToken cancellationToken) =>
        Run(() => GetSummary(session), cancellationToken);

    /// <summary>Saves the summary of its session, replacing an earlier one. A session that does not exist throws <see cref="KeyNotFoundException"/>.</summary>
    public ValueTask SaveSummaryAsync(SessionSummary summary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return Run(() => SaveSummary(summary), cancellationToken);
    }

    /// <summary>Removes the summary of the session; nothing happens when there is none. (Deleting the session removes it through the cascade.)</summary>
    public ValueTask DeleteSummaryAsync(SessionId session, CancellationToken cancellationToken) =>
        Run(() => DeleteSummary(session), cancellationToken);

    private SessionSummary? GetSummary(SessionId session)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT overview, key_points, action_items, model_id, created_at FROM summaries WHERE session_id = $id";
        command.Parameters.AddWithValue("$id", session.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SessionSummary(
            session,
            reader.GetString(0),
            DeserializeItems(reader.GetString(1)),
            DeserializeItems(reader.GetString(2)),
            reader.GetString(3),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)));
    }

    private void SaveSummary(SessionSummary summary)
    {
        using var connection = OpenConnection();

        // The check and the write are one transaction (like SaveTranscript): a session deleted in between is a KeyNotFoundException,
        // not a foreign-key failure of the insert.
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM sessions WHERE id = $id";
            exists.Parameters.AddWithValue("$id", summary.Session.ToString());
            if (exists.ExecuteScalar() is null)
            {
                throw new KeyNotFoundException($"Session {summary.Session} does not exist.");
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO summaries (session_id, overview, key_points, action_items, model_id, created_at) " +
            "VALUES ($id, $overview, $points, $actions, $model, $created) " +
            "ON CONFLICT (session_id) DO UPDATE SET overview = excluded.overview, key_points = excluded.key_points, " +
            "action_items = excluded.action_items, model_id = excluded.model_id, created_at = excluded.created_at";
        command.Parameters.AddWithValue("$id", summary.Session.ToString());
        command.Parameters.AddWithValue("$overview", summary.Overview);
        command.Parameters.AddWithValue("$points", SerializeItems(summary.KeyPoints));
        command.Parameters.AddWithValue("$actions", SerializeItems(summary.ActionItems));
        command.Parameters.AddWithValue("$model", summary.ModelId);
        command.Parameters.AddWithValue("$created", summary.CreatedAt.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private void DeleteSummary(SessionId session)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM summaries WHERE session_id = $id";
        command.Parameters.AddWithValue("$id", session.ToString());
        command.ExecuteNonQuery();
    }

    private static string SerializeItems(IReadOnlyList<SummaryItem> items)
    {
        var dtos = new SummaryItemDto[items.Count];
        for (var i = 0; i < dtos.Length; i++)
        {
            dtos[i] = new SummaryItemDto(items[i].Text, items[i].At is { } at ? (long)at.TotalMilliseconds : null);
        }

        return JsonSerializer.Serialize(dtos, StorageJsonContext.Default.SummaryItemDtoArray);
    }

    private static IReadOnlyList<SummaryItem> DeserializeItems(string json)
    {
        var dtos = JsonSerializer.Deserialize(json, StorageJsonContext.Default.SummaryItemDtoArray);
        if (dtos is null || dtos.Length == 0)
        {
            return [];
        }

        var items = new SummaryItem[dtos.Length];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = new SummaryItem(dtos[i].Text, dtos[i].AtMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);
        }

        return items;
    }
}
