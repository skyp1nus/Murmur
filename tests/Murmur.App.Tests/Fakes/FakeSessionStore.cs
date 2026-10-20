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
                .Where(s => (query.From is null || s.StartedAt >= query.From) && (query.To is null || s.StartedAt <= query.To))
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
