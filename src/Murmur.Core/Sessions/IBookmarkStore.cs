namespace Murmur.Core.Sessions;

/// <summary>A marked moment of a session. <see cref="At"/> is on the session timeline (paused time excluded).</summary>
public sealed record Bookmark(long Id, SessionId Session, TimeSpan At, string Note);

public sealed record NewBookmark(TimeSpan At, string Note = "");

/// <summary>Marked moments of sessions. They go with the session (deleting it deletes its bookmarks).</summary>
public interface IBookmarkStore
{
    /// <summary>Adds bookmarks in one transaction; returns every bookmark of the session, ordered by At.</summary>
    ValueTask<IReadOnlyList<Bookmark>> AddBookmarksAsync(SessionId session, IReadOnlyList<NewBookmark> bookmarks, CancellationToken cancellationToken);

    /// <summary>Ordered by At, then Id.</summary>
    ValueTask<IReadOnlyList<Bookmark>> GetBookmarksAsync(SessionId session, CancellationToken cancellationToken);

    /// <summary>Unknown id throws KeyNotFoundException.</summary>
    ValueTask UpdateBookmarkNoteAsync(long id, string note, CancellationToken cancellationToken);

    /// <summary>Unknown id is a no-op.</summary>
    ValueTask DeleteBookmarkAsync(long id, CancellationToken cancellationToken);
}
