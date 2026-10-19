using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;

namespace Murmur.Storage;

public sealed partial class SqliteStorage
{
    public ValueTask<SessionRecord> CreateSessionAsync(string title, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);
        return Run(() => CreateSession(title, startedAt), cancellationToken);
    }

    public ValueTask UpdateSessionAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Run(() => UpdateSession(session), cancellationToken);
    }

    public ValueTask<SessionRecord?> GetSessionAsync(SessionId id, CancellationToken cancellationToken) =>
        Run(() => GetSession(id), cancellationToken);

    /// <summary>
    /// Newest first. <see cref="SessionQuery.From"/> and <see cref="SessionQuery.To"/> are both inclusive bounds on the
    /// start time; a <see cref="SessionQuery.Limit"/> of zero or less yields an empty list.
    /// </summary>
    public ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(SessionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Run(() => ListSessions(query), cancellationToken);
    }

    public ValueTask DeleteSessionAsync(SessionId id, CancellationToken cancellationToken) =>
        Run(() => DeleteSession(id), cancellationToken);

    /// <summary>
    /// Absolute session folder. Works for sessions created by an earlier run (the folder is looked up in the database
    /// and cached; it is stored relative to the data root, or absolute when it lies outside). Throws <see cref="KeyNotFoundException"/> for an unknown session.
    /// </summary>
    public SessionFiles GetFiles(SessionId id)
    {
        if (!_folders.TryGetValue(id, out var relative))
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT folder FROM sessions WHERE id = $id";
            command.Parameters.AddWithValue("$id", id.ToString());
            relative = command.ExecuteScalar() as string
                ?? throw new KeyNotFoundException($"Session {id} does not exist.");
            _folders[id] = relative;
        }

        return new SessionFiles(ResolveFolder(relative));
    }

    private SessionRecord CreateSession(string title, DateTimeOffset startedAt)
    {
        var started = DateTimeOffset.FromUnixTimeMilliseconds(startedAt.ToUnixTimeMilliseconds());
        var id = SessionId.New();
        using var connection = OpenConnection();

        var relative = ReserveFolder(id, started);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO sessions (id, title, started_at, duration_ms, state, has_mic, has_system, screen_segments, folder, error) " +
                "VALUES ($id, $title, $started, 0, $state, 0, 0, '[]', $folder, NULL)";
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$started", started.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$state", (int)SessionState.Recording);
            command.Parameters.AddWithValue("$folder", relative);
            command.ExecuteNonQuery();
        }
        catch
        {
            TryDeleteDirectory(ResolveFolder(relative));
            throw;
        }

        _folders[id] = relative;
        return new SessionRecord
        {
            Id = id,
            Title = title,
            StartedAt = started.ToLocalTime(),
            State = SessionState.Recording,
        };
    }

    private void UpdateSession(SessionRecord session)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE sessions SET title = $title, started_at = $started, duration_ms = $duration, state = $state, " +
            "has_mic = $mic, has_system = $system, screen_segments = $segments, error = $error WHERE id = $id";
        command.Parameters.AddWithValue("$id", session.Id.ToString());
        command.Parameters.AddWithValue("$title", session.Title);
        command.Parameters.AddWithValue("$started", session.StartedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$duration", (long)session.Duration.TotalMilliseconds);
        command.Parameters.AddWithValue("$state", (int)session.State);
        command.Parameters.AddWithValue("$mic", session.HasMicrophone ? 1 : 0);
        command.Parameters.AddWithValue("$system", session.HasSystemAudio ? 1 : 0);
        command.Parameters.AddWithValue("$segments", SerializeSegments(session.ScreenSegments));
        command.Parameters.AddWithValue("$error", (object?)session.Error ?? DBNull.Value);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new KeyNotFoundException($"Session {session.Id} does not exist.");
        }
    }

    private SessionRecord? GetSession(SessionId id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + SessionColumns + " FROM sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    private IReadOnlyList<SessionRecord> ListSessions(SessionQuery query)
    {
        if (query.Limit <= 0)
        {
            return [];
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT " + SessionColumns + " FROM sessions " +
            "WHERE ($from IS NULL OR started_at >= $from) AND ($to IS NULL OR started_at <= $to) " +
            "ORDER BY started_at DESC, id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$from", query.From is { } from ? from.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue("$to", query.To is { } to ? to.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue("$limit", query.Limit);

        var result = new List<SessionRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadSession(reader));
        }

        return result;
    }

    /// <summary>Folder removal seam; tests replace it to simulate a locked file on non-Windows hosts.</summary>
    internal Action<string> DeleteFolder { get; set; } = static folder => Directory.Delete(folder, recursive: true);

    private void DeleteSession(SessionId id)
    {
        // Folder first, row second: if the folder cannot be removed (a file still open on Windows), the row stays
        // and the caller can retry. The reverse order would orphan the folder with no way to find it again.
        string? relative;
        using (var connection = OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT folder FROM sessions WHERE id = $id";
            command.Parameters.AddWithValue("$id", id.ToString());
            relative = command.ExecuteScalar() as string;
        }

        if (relative is null)
        {
            _folders.TryGetValue(id, out relative);
        }

        if (relative is not null)
        {
            var folder = ResolveFolder(relative);
            EnsureDeletable(relative, folder);
            if (Directory.Exists(folder))
            {
                DeleteFolder(folder);
            }
        }

        // The cascade removes the chunk rows; the vector cache entry goes in the same critical section so a
        // concurrent search can never load rows that are about to disappear and keep them cached.
        lock (_vectorLock)
        {
            using (var connection = OpenConnection())
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM sessions WHERE id = $id";
                command.Parameters.AddWithValue("$id", id.ToString());
                command.ExecuteNonQuery();
            }

            InvalidateVectors(id);
        }

        _folders.TryRemove(id, out _);
    }

    private static SessionRecord ReadSession(SqliteDataReader reader) => new()
    {
        Id = SessionId.Parse(reader.GetString(0)),
        Title = reader.GetString(1),
        StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)).ToLocalTime(),
        Duration = TimeSpan.FromMilliseconds(reader.GetInt64(3)),
        State = (SessionState)reader.GetInt32(4),
        HasMicrophone = reader.GetInt32(5) != 0,
        HasSystemAudio = reader.GetInt32(6) != 0,
        ScreenSegments = DeserializeSegments(reader.GetString(7)),
        Error = reader.IsDBNull(8) ? null : reader.GetString(8),
    };

    private static string SerializeSegments(IReadOnlyList<ScreenSegment> segments)
    {
        var dtos = new ScreenSegmentDto[segments.Count];
        for (var i = 0; i < dtos.Length; i++)
        {
            var segment = segments[i];
            dtos[i] = new ScreenSegmentDto(
                segment.Index,
                (long)segment.Offset.TotalMilliseconds,
                (long)segment.Duration.TotalMilliseconds);
        }

        return JsonSerializer.Serialize(dtos, StorageJsonContext.Default.ScreenSegmentDtoArray);
    }

    private static IReadOnlyList<ScreenSegment> DeserializeSegments(string json)
    {
        var dtos = JsonSerializer.Deserialize(json, StorageJsonContext.Default.ScreenSegmentDtoArray);
        if (dtos is null || dtos.Length == 0)
        {
            return [];
        }

        var segments = new ScreenSegment[dtos.Length];
        for (var i = 0; i < dtos.Length; i++)
        {
            var dto = dtos[i];
            segments[i] = new ScreenSegment(dto.Index, TimeSpan.FromMilliseconds(dto.OffsetMs), TimeSpan.FromMilliseconds(dto.DurationMs));
        }

        return segments;
    }

    /// <summary>
    /// Creates <c>yyyy-MM-dd_HH-mm-ss_&lt;first 8 chars of id&gt;</c> (local time) inside the configured recordings folder
    /// (the default <c>Recordings</c> when none is configured or the configured one is unusable) and returns the value for the
    /// <c>sessions.folder</c> column: relative to the data root with <c>/</c> separators when the folder lies under the root,
    /// otherwise the absolute path.
    /// The id prefix of a UUIDv7 is its timestamp, so two sessions in the same second can share a name; a numeric suffix keeps
    /// the folders distinct.
    /// </summary>
    private string ReserveFolder(SessionId id, DateTimeOffset startedAt)
    {
        var stamp = startedAt.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var name = stamp + "_" + id.ToString()[..8];
        var requested = _recordingsFolder?.Invoke();

        string full;
        RecordingsFolderFallbackInfo? fallback = null;
        lock (_folderLock)
        {
            var useDefault = true;
            full = string.Empty;
            if (!string.IsNullOrWhiteSpace(requested))
            {
                var trimmed = requested.Trim();
                var reason = TryReserve(trimmed, name, out full);
                if (reason is null)
                {
                    useDefault = false;
                }
                else
                {
                    fallback = new RecordingsFolderFallbackInfo(id, trimmed, reason);
                }
            }

            if (useDefault)
            {
                // The default folder is ours; a failure here is a real error and propagates.
                full = CreateUnique(_paths.RecordingsDirectory, name);
            }
        }

        _lastFallback = fallback;
        if (fallback is not null)
        {
            RaiseFallback(fallback);
        }

        return ToStoredFolder(full);
    }

    /// <summary>Returns <c>null</c> on success (the new folder in <paramref name="created"/>), otherwise why the folder is unusable.</summary>
    private static string? TryReserve(string folder, string name, out string created)
    {
        created = string.Empty;
        if (!Path.IsPathFullyQualified(folder))
        {
            return "The recordings folder is not an absolute path.";
        }

        try
        {
            created = CreateUnique(Path.GetFullPath(folder), name);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    /// <summary>Creates <paramref name="name"/> (or <c>name_N</c>) inside <paramref name="parent"/>, creating the parent if needed.</summary>
    private static string CreateUnique(string parent, string name)
    {
        var candidate = name;
        for (var n = 2; Directory.Exists(Path.Combine(parent, candidate)); n++)
        {
            candidate = name + "_" + n.ToString(CultureInfo.InvariantCulture);
        }

        var path = Path.Combine(parent, candidate);
        Directory.CreateDirectory(path);
        return path;
    }

    private void RaiseFallback(RecordingsFolderFallbackInfo fallback)
    {
        try
        {
            RecordingsFolderFallback?.Invoke(fallback);
        }
        catch (Exception)
        {
            // A faulty subscriber must not stop a recording from starting.
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private string ToStoredFolder(string full)
    {
        var root = Path.TrimEndingDirectorySeparator(_paths.Root) + Path.DirectorySeparatorChar;
        if (full.StartsWith(root, PathComparison))
        {
            return full[root.Length..].Replace(Path.DirectorySeparatorChar, '/');
        }

        return full;
    }

    /// <summary>
    /// Maps a <c>sessions.folder</c> value to an absolute path. A relative value must stay under the data root
    /// (a <c>..</c> that escapes it is refused); a fully qualified value is a folder outside the root and is taken as is.
    /// </summary>
    private string ResolveFolder(string stored)
    {
        if (Path.IsPathFullyQualified(stored))
        {
            return Path.GetFullPath(stored);
        }

        if (Path.IsPathRooted(stored))
        {
            throw new InvalidDataException($"Session folder '{stored}' is neither relative nor absolute.");
        }

        var full = Path.GetFullPath(Path.Combine(_paths.Root, stored.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.TrimEndingDirectorySeparator(_paths.Root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Session folder '{stored}' points outside the data root.");
        }

        return full;
    }

    /// <summary>
    /// Delete guard for a folder stored as an absolute path. A row is plain data in a user-writable database, so a
    /// tampered value (the user's home, a drive root) must never turn "delete session" into "delete that folder tree".
    /// Only a path whose last segment has the shape <see cref="ReserveFolder"/> produces
    /// (<c>yyyy-MM-dd_HH-mm-ss_&lt;8 hex&gt;</c> with an optional <c>_N</c> suffix) and that is not a drive/filesystem root
    /// is deleted; anything else is refused with <see cref="InvalidDataException"/> and nothing is touched.
    /// Relative values are already confined to the data root by <see cref="ResolveFolder"/>.
    /// </summary>
    private static void EnsureDeletable(string stored, string resolved)
    {
        if (!Path.IsPathFullyQualified(stored))
        {
            return;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(resolved);
        var root = Path.GetPathRoot(resolved);
        if (root is null || Path.TrimEndingDirectorySeparator(root).Length >= trimmed.Length
            || !IsSessionFolderName(Path.GetFileName(trimmed)))
        {
            throw new InvalidDataException($"Refusing to delete '{stored}': it is not a session folder created by Murmur.");
        }
    }

    /// <summary>Whether <paramref name="name"/> looks like <c>yyyy-MM-dd_HH-mm-ss_&lt;8 hex&gt;</c> optionally followed by <c>_N</c>.</summary>
    internal static bool IsSessionFolderName(string name)
    {
        const string Pattern = "dddd-dd-dd_dd-dd-dd_";
        const int HexLength = 8;
        const int Prefix = 20;
        if (name.Length < Prefix + HexLength)
        {
            return false;
        }

        for (var i = 0; i < Prefix; i++)
        {
            var ok = Pattern[i] == 'd' ? char.IsAsciiDigit(name[i]) : name[i] == Pattern[i];
            if (!ok)
            {
                return false;
            }
        }

        for (var i = Prefix; i < Prefix + HexLength; i++)
        {
            if (!char.IsAsciiHexDigit(name[i]))
            {
                return false;
            }
        }

        var rest = name.AsSpan(Prefix + HexLength);
        if (rest.IsEmpty)
        {
            return true;
        }

        if (rest.Length < 2 || rest[0] != '_')
        {
            return false;
        }

        foreach (var c in rest[1..])
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
