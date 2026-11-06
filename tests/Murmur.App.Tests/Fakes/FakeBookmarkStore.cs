using Murmur.Core.Sessions;

namespace Murmur.App.Tests.Fakes;

/// <summary><see cref="IBookmarkStore"/> in memory: what the recording marks save when a recording stops.</summary>
internal sealed class FakeBookmarkStore : IBookmarkStore
{
    private readonly Lock _gate = new();
    private readonly List<Bookmark> _all = [];
    private long _nextId = 1;

    /// <summary>When set, <see cref="AddBookmarksAsync"/> throws it (a locked or full database).</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, <see cref="GetBookmarksAsync"/> throws it.</summary>
    public Exception? GetFailure { get; set; }

    /// <summary>When set, <see cref="UpdateBookmarkNoteAsync"/> throws it.</summary>
    public Exception? UpdateFailure { get; set; }

    /// <summary>When set, <see cref="DeleteBookmarkAsync"/> throws it.</summary>
    public Exception? DeleteFailure { get; set; }

    /// <summary>Every <see cref="UpdateBookmarkNoteAsync"/> call in order: (id, note).</summary>
    public List<(long Id, string Note)> Updates { get; } = [];

    /// <summary>Puts bookmarks in the store as they were saved earlier (returns their ids).</summary>
    public long[] Put(SessionId session, params (double Seconds, string Note)[] bookmarks)
    {
        lock (_gate)
        {
            var ids = new long[bookmarks.Length];
            for (var i = 0; i < bookmarks.Length; i++)
            {
                ids[i] = _nextId;
                _all.Add(new Bookmark(_nextId++, session, TimeSpan.FromSeconds(bookmarks[i].Seconds), bookmarks[i].Note));
            }

            return ids;
        }
    }

    public IReadOnlyList<Bookmark> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _all];
            }
        }
    }

    public ValueTask<IReadOnlyList<Bookmark>> AddBookmarksAsync(SessionId session, IReadOnlyList<NewBookmark> bookmarks, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            foreach (var bookmark in bookmarks)
            {
                _all.Add(new Bookmark(_nextId++, session, bookmark.At, bookmark.Note));
            }

            return ValueTask.FromResult<IReadOnlyList<Bookmark>>([.. _all.Where(b => b.Session == session).OrderBy(b => b.At).ThenBy(b => b.Id)]);
        }
    }

    public ValueTask<IReadOnlyList<Bookmark>> GetBookmarksAsync(SessionId session, CancellationToken cancellationToken)
    {
        if (GetFailure is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<Bookmark>>([.. _all.Where(b => b.Session == session).OrderBy(b => b.At).ThenBy(b => b.Id)]);
        }
    }

    public ValueTask UpdateBookmarkNoteAsync(long id, string note, CancellationToken cancellationToken)
    {
        if (UpdateFailure is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            var index = _all.FindIndex(b => b.Id == id);
            if (index < 0)
            {
                throw new KeyNotFoundException();
            }

            _all[index] = _all[index] with { Note = note };
            Updates.Add((id, note));
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask DeleteBookmarkAsync(long id, CancellationToken cancellationToken)
    {
        if (DeleteFailure is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            _all.RemoveAll(b => b.Id == id);
            return ValueTask.CompletedTask;
        }
    }
}
