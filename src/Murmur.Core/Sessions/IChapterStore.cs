namespace Murmur.Core.Sessions;

/// <summary>A titled part of a session; <see cref="Start"/> is on the session timeline.</summary>
public sealed record Chapter(string Title, TimeSpan Start);

/// <summary>The stored chapters of a session. They go with the session when it is deleted.</summary>
public interface IChapterStore
{
    /// <summary>Replaces the chapters of a session (empty list = no chapters). Ordered by Start when read back.</summary>
    ValueTask SaveChaptersAsync(SessionId session, IReadOnlyList<Chapter> chapters, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Chapter>> GetChaptersAsync(SessionId session, CancellationToken cancellationToken);
}
