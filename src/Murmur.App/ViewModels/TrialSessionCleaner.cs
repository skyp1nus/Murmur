using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>
/// Knows which sessions are the onboarding's trial recordings and removes the ones an earlier run left behind (the window was
/// closed while the pipeline still worked on the trial, so its deferred deletion was lost).
/// </summary>
/// <remarks>
/// A trial is recognised by an invisible marker at the end of its title (<see cref="Marker"/>, no Core change needed) or, for
/// a title that lost it, by the exact trial title in either UI language.
/// </remarks>
public sealed class TrialSessionCleaner
{
    /// <summary>U+2063 INVISIBLE SEPARATOR: appended to the title of a trial session, never shown, survives the store.</summary>
    public const string Marker = "⁣";

    private readonly ISessionStore _sessions;
    private readonly ISessionProcessing? _processing;

    public TrialSessionCleaner(ISessionStore sessions, ISessionProcessing? processing)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
        _processing = processing;
    }

    /// <summary>The title the trial recording gets: the localized name plus the marker.</summary>
    public static string TitleOfTrial() => Strings.Onb5_TrialTitle + Marker;

    /// <summary>The session is a trial recording of the onboarding.</summary>
    public static bool IsTrial(SessionRecord session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var title = session.Title;
        return title.EndsWith(Marker, StringComparison.Ordinal)
            || string.Equals(title, Strings.Get("Onb5_TrialTitle", "uk"), StringComparison.Ordinal)
            || string.Equals(title, Strings.Get("Onb5_TrialTitle", "en"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Deletes the trial sessions the pipeline is not working on: no status, a queued job (it finds the session gone and drops
    /// out) or a final status. One that is being transcribed right now is left for the next start.
    /// </summary>
    /// <returns>How many were deleted.</returns>
    public async Task<int> RemoveLeftoversAsync()
    {
        var removed = 0;
        try
        {
            foreach (var session in await ListAsync().ConfigureAwait(false))
            {
                if (!IsTrial(session) || !IsSafeToDelete(session.Id))
                {
                    continue;
                }

                await _sessions.DeleteSessionAsync(session.Id, CancellationToken.None).ConfigureAwait(false);
                removed++;
            }
        }
        catch (Exception)
        {
            // Best effort: a trial that stays is an ordinary recording in the library.
        }

        return removed;
    }

    /// <summary>The library has a session that is not a trial: the user has been recording before.</summary>
    public async Task<bool> HasOtherSessionsAsync()
    {
        try
        {
            return (await ListAsync().ConfigureAwait(false)).Any(s => !IsTrial(s));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<SessionRecord>> ListAsync() =>
        await _sessions.ListSessionsAsync(new SessionQuery(Limit: int.MaxValue), CancellationToken.None).ConfigureAwait(false);

    private bool IsSafeToDelete(SessionId id) =>
        _processing?.GetStatus(id) is not { } status
        || status.Stage is SessionProcessingStage.Queued or SessionProcessingStage.Completed or SessionProcessingStage.Failed or SessionProcessingStage.ModelMissing;
}
