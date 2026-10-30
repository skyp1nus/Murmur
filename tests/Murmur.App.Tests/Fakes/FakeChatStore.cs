using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IChatStore"/> with the semantics of the SQLite one (order by last update, replace at an index, cascade on a deleted
/// recording through <see cref="DeleteSession"/>). Its clock ticks one second per write, so the order of threads is deterministic.
/// </summary>
internal sealed class FakeChatStore : IChatStore
{
    private readonly List<ChatThread> _threads = [];
    private readonly Dictionary<ChatThreadId, List<ChatMessage>> _messages = [];
    private DateTimeOffset _clock;

    public FakeChatStore(DateTimeOffset? start = null) => _clock = start ?? new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    /// <summary>When set, every call throws it (a locked or full database).</summary>
    public Exception? Failure { get; set; }

    /// <summary>Sessions that exist: a thread cannot be scoped to another one. <c>null</c> = any.</summary>
    public HashSet<SessionId>? KnownSessions { get; set; }

    public int ListCalls { get; private set; }

    public IReadOnlyList<ChatThread> Threads => [.. _threads.OrderByDescending(t => t.UpdatedAt)];

    public IReadOnlyList<ChatMessage> MessagesOf(ChatThreadId thread) => _messages.TryGetValue(thread, out var list) ? [.. list] : [];

    private DateTimeOffset Tick() => _clock += TimeSpan.FromSeconds(1);

    private void ThrowIfFailing()
    {
        if (Failure is { } failure)
        {
            throw failure;
        }
    }

    /// <summary>A recording was deleted: the threads scoped to it go with it (their messages too), citations of it stay as they are.</summary>
    public void DeleteSession(SessionId id)
    {
        foreach (var thread in _threads.Where(t => t.Scope == id).ToArray())
        {
            _threads.Remove(thread);
            _messages.Remove(thread.Id);
        }
    }

    public ValueTask<IReadOnlyList<ChatThread>> ListThreadsAsync(int limit, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        ListCalls++;
        return ValueTask.FromResult<IReadOnlyList<ChatThread>>([.. Threads.Take(Math.Max(0, limit))]);
    }

    public ValueTask<ChatThread> CreateThreadAsync(string title, SessionId? scope, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        if (scope is { } session && KnownSessions is { } known && !known.Contains(session))
        {
            throw new KeyNotFoundException($"Session {session} does not exist.");
        }

        var thread = new ChatThread(ChatThreadId.New(), title, scope, Tick());
        _threads.Add(thread);
        _messages[thread.Id] = [];
        return ValueTask.FromResult(thread);
    }

    public ValueTask<IReadOnlyList<ChatMessage>> GetMessagesAsync(ChatThreadId thread, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(MessagesOf(thread));
    }

    public ValueTask AppendMessageAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        var index = _threads.FindIndex(t => t.Id == message.Thread);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Chat thread {message.Thread} does not exist.");
        }

        var list = _messages[message.Thread];
        if (message.Index < 0 || message.Index > list.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(message));
        }

        list.RemoveRange(message.Index, list.Count - message.Index);
        list.Add(message);
        _threads[index] = _threads[index] with { UpdatedAt = Tick() };
        return ValueTask.CompletedTask;
    }

    public ValueTask RenameThreadAsync(ChatThreadId thread, string title, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        var index = _threads.FindIndex(t => t.Id == thread);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Chat thread {thread} does not exist.");
        }

        _threads[index] = _threads[index] with { Title = title };
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteThreadAsync(ChatThreadId thread, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        _threads.RemoveAll(t => t.Id == thread);
        _messages.Remove(thread);
        return ValueTask.CompletedTask;
    }
}
