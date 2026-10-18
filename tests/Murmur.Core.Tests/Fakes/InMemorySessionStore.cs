using Murmur.Core.Sessions;

namespace Murmur.Core.Tests.Fakes;

/// <summary>
/// Minimal <see cref="ISessionStore"/> for tests: rows in memory, one folder per session under <see cref="Root"/>.
/// Also compiled into the crash-test helper (internal, so the two copies never clash).
/// </summary>
internal sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<SessionId, SessionRecord> _sessions = [];
    private readonly List<SessionRecord> _updates = [];

    public InMemorySessionStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }

    /// <summary>Every record passed to <see cref="UpdateSessionAsync"/>, in order.</summary>
    public IReadOnlyList<SessionRecord> Updates
    {
        get
        {
            lock (_sessions)
            {
                return [.. _updates];
            }
        }
    }

    public void Seed(SessionRecord record)
    {
        lock (_sessions)
        {
            _sessions[record.Id] = record;
        }
    }

    public SessionRecord? Get(SessionId id)
    {
        lock (_sessions)
        {
            return _sessions.GetValueOrDefault(id);
        }
    }

    public ValueTask<SessionRecord> CreateSessionAsync(string title, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var record = new SessionRecord { Id = SessionId.New(), Title = title, StartedAt = startedAt, State = SessionState.Recording };
        Directory.CreateDirectory(GetFiles(record.Id).Directory);
        Seed(record);
        return ValueTask.FromResult(record);
    }

    public ValueTask UpdateSessionAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        lock (_sessions)
        {
            _sessions[session.Id] = session;
            _updates.Add(session);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<SessionRecord?> GetSessionAsync(SessionId id, CancellationToken cancellationToken) => ValueTask.FromResult(Get(id));

    public ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(SessionQuery query, CancellationToken cancellationToken)
    {
        lock (_sessions)
        {
            IReadOnlyList<SessionRecord> list = [.. _sessions.Values
                .Where(s => (query.From is null || s.StartedAt >= query.From) && (query.To is null || s.StartedAt <= query.To))
                .OrderByDescending(s => s.StartedAt)
                .Take(query.Limit)];
            return ValueTask.FromResult(list);
        }
    }

    public ValueTask DeleteSessionAsync(SessionId id, CancellationToken cancellationToken)
    {
        lock (_sessions)
        {
            _sessions.Remove(id);
        }

        var dir = GetFiles(id).Directory;
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    public SessionFiles GetFiles(SessionId id) => new(Path.Combine(Root, id.ToString()));
}
