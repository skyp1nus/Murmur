using Murmur.Core.Sessions;

namespace Murmur.Core.Tests.Fakes;

/// <summary><see cref="IBookmarkStore"/> in memory, with a switch to make every save fail and an optional gate to hold a save.</summary>
internal sealed class InMemoryBookmarkStore : IBookmarkStore
{
    private readonly Lock _gate = new();
    private readonly List<Bookmark> _all = [];
    private long _nextId = 1;

    /// <summary>When set, <see cref="AddBookmarksAsync"/> throws it.</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, <see cref="AddBookmarksAsync"/> waits for it before it stores anything.</summary>
    public Task? Gate { get; set; }

    public int Adds { get; private set; }

    public async ValueTask<IReadOnlyList<Bookmark>> AddBookmarksAsync(SessionId session, IReadOnlyList<NewBookmark> bookmarks, CancellationToken cancellationToken)
    {
        if (Gate is { } gate)
        {
            await gate;
        }

        lock (_gate)
        {
            Adds++;
            if (Failure is { } failure)
            {
                throw failure;
            }

            foreach (var bookmark in bookmarks)
            {
                _all.Add(new Bookmark(_nextId++, session, bookmark.At, bookmark.Note));
            }

            return Of(session);
        }
    }

    public ValueTask<IReadOnlyList<Bookmark>> GetBookmarksAsync(SessionId session, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(Of(session));
        }
    }

    public ValueTask UpdateBookmarkNoteAsync(long id, string note, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var index = _all.FindIndex(b => b.Id == id);
            if (index < 0)
            {
                throw new KeyNotFoundException();
            }

            _all[index] = _all[index] with { Note = note };
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask DeleteBookmarkAsync(long id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _all.RemoveAll(b => b.Id == id);
            return ValueTask.CompletedTask;
        }
    }

    private IReadOnlyList<Bookmark> Of(SessionId session) => [.. _all.Where(b => b.Session == session).OrderBy(b => b.At).ThenBy(b => b.Id)];
}
