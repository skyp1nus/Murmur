using Murmur.Core.Sessions;

namespace Murmur.App.Tests.Fakes;

/// <summary>In-memory <see cref="ISessionStore"/> that honours <see cref="SessionQuery"/> (newest first).</summary>
internal sealed class FakeSessionStore : ISessionStore
{
    private readonly List<SessionRecord> _sessions = [];

    public Exception? ListFailure { get; set; }

    public List<SessionQuery> Queries { get; } = [];

    public string Root { get; init; } = Path.Combine(Path.GetTempPath(), "murmur-fake-store");

    public void Add(SessionRecord session) => _sessions.Add(session);

    /// <summary>Replaces the stored session with the same id (what a background recovery does to a row).</summary>
    public void Replace(SessionRecord session)
    {
        var index = _sessions.FindIndex(s => s.Id == session.Id);
        _sessions[index] = session;
    }

    public ValueTask<SessionRecord> CreateSessionAsync(string title, DateTimeOffset startedAt, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask UpdateSessionAsync(SessionRecord session, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>Every rename, in order.</summary>
    public List<(SessionId Id, string Title)> Renames { get; } = [];

    /// <summary>When set, <see cref="RenameSessionAsync"/> throws it.</summary>
    public Exception? RenameFailure { get; set; }

    /// <summary>When set, <see cref="RenameSessionAsync"/> waits for this task before it writes (a slow database).</summary>
    public Task? RenameGate { get; set; }

    public async ValueTask RenameSessionAsync(SessionId id, string title, CancellationToken cancellationToken)
    {
        if (RenameGate is { } gate)
        {
            await gate;
        }

        if (RenameFailure is { } failure)
        {
            throw failure;
        }

        var index = _sessions.FindIndex(s => s.Id == id);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Session {id} does not exist.");
        }

        _sessions[index] = _sessions[index] with { Title = title };
        Renames.Add((id, title));
    }

    public SessionRecord? Get(SessionId id) => _sessions.Find(s => s.Id == id);

    public ValueTask<SessionRecord?> GetSessionAsync(SessionId id, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_sessions.Find(s => s.Id == id));

    public ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(SessionQuery query, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        if (ListFailure is { } failure)
        {
            throw failure;
        }

        IReadOnlyList<SessionRecord> result =
        [
            .. _sessions
                .Where(s => (query.From is null || s.StartedAt >= query.From) && (query.To is null || s.StartedAt < query.To))
                .OrderByDescending(s => s.StartedAt)
                .Take(query.Limit),
        ];
        return ValueTask.FromResult(result);
    }

    /// <summary>Ids passed to <see cref="DeleteSessionAsync"/>, in order.</summary>
    public List<SessionId> Deleted { get; } = [];

    public ValueTask DeleteSessionAsync(SessionId id, CancellationToken cancellationToken)
    {
        Deleted.Add(id);
        _sessions.RemoveAll(s => s.Id == id);
        return ValueTask.CompletedTask;
    }

    public SessionFiles GetFiles(SessionId id) => new(Path.Combine(Root, id.ToString()));
}
