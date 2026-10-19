using Murmur.Core.Audio;
using Murmur.Core.Sessions;

namespace Murmur.Core.Recording;

/// <summary>
/// Run once at app start, before anything records: every session still in <see cref="SessionState.Recording"/>
/// belongs to a process that died. Its WAV headers are repaired from the real file lengths, the duration is taken from
/// the WAVs and the session becomes <see cref="SessionState.Interrupted"/> (or <see cref="SessionState.Failed"/> when
/// no audio could be recovered at all: every track file is missing or not a valid WAV).
/// </summary>
/// <remarks>
/// Right after a crash an antivirus scan, the indexer or the dying process itself can still hold a track file. A busy
/// file is retried with a short backoff; if it stays busy the session is left untouched in
/// <see cref="SessionState.Recording"/> (so the next start tries again), kept out of the returned list and reported
/// through <see cref="BusySessions"/>. Busy never counts as "no audio".
/// </remarks>
public sealed class RecoveryService
{
    /// <summary>Pauses between attempts to open a busy file (total about 2.5 s per file).</summary>
    internal static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(1000),
    ];

    private readonly ISessionStore _store;
    private readonly Func<string, Stream> _open;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private IReadOnlyList<SessionId> _busySessions = [];

    public RecoveryService(ISessionStore store)
        : this(store, null, null)
    {
    }

    /// <summary>Test seam: how files are opened for repair and how the backoff waits.</summary>
    internal RecoveryService(ISessionStore store, Func<string, Stream>? open, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _open = open ?? (static path => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read));
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Sessions the last <see cref="RecoverAsync"/> call skipped because a track file stayed busy after all retries.
    /// They are still <see cref="SessionState.Recording"/> in the store and will be retried at the next start.
    /// </summary>
    public IReadOnlyList<SessionId> BusySessions => Volatile.Read(ref _busySessions);

    /// <returns>The sessions that were updated, as stored (busy sessions are not included, see <see cref="BusySessions"/>).</returns>
    public async ValueTask<IReadOnlyList<SessionRecord>> RecoverAsync(CancellationToken cancellationToken)
    {
        var sessions = await _store.ListSessionsAsync(new SessionQuery(Limit: int.MaxValue), cancellationToken).ConfigureAwait(false);
        var recovered = new List<SessionRecord>();
        var busy = new List<SessionId>();

        foreach (var session in sessions)
        {
            if (session.State != SessionState.Recording)
            {
                continue;
            }

            var files = _store.GetFiles(session.Id);
            var (micResult, micDuration) = await RepairWithRetryAsync(files.MicrophoneAudio, cancellationToken).ConfigureAwait(false);
            var (systemResult, systemDuration) = await RepairWithRetryAsync(files.SystemAudio, cancellationToken).ConfigureAwait(false);

            if (micResult == WavRepairResult.Busy || systemResult == WavRepairResult.Busy)
            {
                busy.Add(session.Id);
                continue;
            }

            var hasMicrophone = micResult == WavRepairResult.Repaired;
            var hasSystem = systemResult == WavRepairResult.Repaired;
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

        Volatile.Write(ref _busySessions, busy.ToArray());
        return recovered;
    }

    private async ValueTask<(WavRepairResult Result, TimeSpan Duration)> RepairWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = WavRepair.Repair(path, _open, out var duration);
            if (result != WavRepairResult.Busy || attempt >= Backoff.Length)
            {
                return (result, duration);
            }

            await _delay(Backoff[attempt], cancellationToken).ConfigureAwait(false);
        }
    }
}
