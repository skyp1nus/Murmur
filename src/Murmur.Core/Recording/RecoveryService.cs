using Murmur.Core.Audio;
using Murmur.Core.Sessions;

namespace Murmur.Core.Recording;

/// <summary>
/// Run once at app start, before anything records: every session still in <see cref="SessionState.Recording"/>
/// belongs to a process that died. Its WAV headers are repaired from the real file lengths, the duration is taken from
/// the WAVs and the session becomes <see cref="SessionState.Interrupted"/> (or <see cref="SessionState.Failed"/> when
/// no audio could be recovered at all).
/// </summary>
public sealed class RecoveryService
{
    private readonly ISessionStore _store;

    public RecoveryService(ISessionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <returns>The sessions that were updated, as stored.</returns>
    public async ValueTask<IReadOnlyList<SessionRecord>> RecoverAsync(CancellationToken cancellationToken)
    {
        var sessions = await _store.ListSessionsAsync(new SessionQuery(Limit: int.MaxValue), cancellationToken).ConfigureAwait(false);
        var recovered = new List<SessionRecord>();

        foreach (var session in sessions)
        {
            if (session.State != SessionState.Recording)
            {
                continue;
            }

            var files = _store.GetFiles(session.Id);
            var hasMicrophone = WavRepair.TryRepair(files.MicrophoneAudio, out var micDuration);
            var hasSystem = WavRepair.TryRepair(files.SystemAudio, out var systemDuration);
            var duration = micDuration > systemDuration ? micDuration : systemDuration;

            var updated = session with
            {
                Duration = duration,
                HasMicrophone = hasMicrophone,
                HasSystemAudio = hasSystem,
                State = hasMicrophone || hasSystem ? SessionState.Interrupted : SessionState.Failed,
                Error = hasMicrophone || hasSystem ? null : "The app stopped while recording and no audio could be recovered.",
            };

            await _store.UpdateSessionAsync(updated, cancellationToken).ConfigureAwait(false);
            recovered.Add(updated);
        }

        return recovered;
    }
}
