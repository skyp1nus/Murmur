using Murmur.Core.Sessions;

namespace Murmur.App.Tests.Fakes;

/// <summary><see cref="IBookmarkStore"/> in memory: what the recording marks save when a recording stops.</summary>
internal sealed class FakeBookmarkStore : IBookmarkStore
{
    private readonly Lock _gate = new();
    private readonly List<Bookmark> _all = [];
    private long _nextId = 1;

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
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<Bookmark>>([.. _all.Where(b => b.Session == session).OrderBy(b => b.At).ThenBy(b => b.Id)]);
        }
    }

    public ValueTask UpdateBookmarkNoteAsync(long id, string note, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask DeleteBookmarkAsync(long id, CancellationToken cancellationToken) => throw new NotSupportedException();
}
