using Murmur.Core.Intelligence;

namespace Murmur.Core.Sessions;

/// <summary>The stored summary of a session: at most one per session, replaced when it is generated again.</summary>
public interface ISummaryStore
{
    /// <summary>The summary of the session, or <c>null</c> when none was saved.</summary>
    ValueTask<SessionSummary?> GetSummaryAsync(SessionId session, CancellationToken cancellationToken);

    /// <summary>Saves the summary, replacing an earlier one. A session that does not exist throws <see cref="KeyNotFoundException"/>.</summary>
    ValueTask SaveSummaryAsync(SessionSummary summary, CancellationToken cancellationToken);

    /// <summary>Removes the summary of the session; nothing happens when there is none. Deleting the session removes it too.</summary>
    ValueTask DeleteSummaryAsync(SessionId session, CancellationToken cancellationToken);
}
