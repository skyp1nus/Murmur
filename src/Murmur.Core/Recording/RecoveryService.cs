using System.Collections.Immutable;
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
/// file is retried with a short backoff, within one <see cref="RetryBudget"/> for the whole <see cref="RecoverAsync"/>
/// call (once it is spent every remaining file gets a single attempt, so start-up is never held up for long). If a
/// file stays busy the session is left untouched in <see cref="SessionState.Recording"/>, kept out of the returned list
/// and reported through <see cref="BusySessions"/>. Busy never counts as "no audio". The host can then call
/// <see cref="RetryBusyInBackgroundAsync"/>, which re-runs only those sessions later; otherwise the next start tries again.
/// </remarks>
public sealed class RecoveryService
{
    /// <summary>Total time <see cref="RecoverAsync"/> (or one background pass) may spend waiting for busy files.</summary>
    public static readonly TimeSpan RetryBudget = TimeSpan.FromSeconds(3);

    /// <summary>Pauses between attempts to open a busy file (2.55 s in total).</summary>
    internal static readonly ImmutableArray<TimeSpan> Backoff =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(1000),
    ];

    /// <summary>Waits before each background pass of <see cref="RetryBusyInBackgroundAsync"/>.</summary>
    internal static readonly ImmutableArray<TimeSpan> BackgroundSchedule =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
    ];

    private readonly ISessionStore _store;
    private readonly TimeProvider _time;
    private readonly Func<string, Stream> _open;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<SessionId> _busySessions = [];

    public RecoveryService(ISessionStore store, TimeProvider? time = null)
        : this(store, time, null, null)
    {
    }

    /// <summary>Test seam: how files are opened for repair and how the backoff waits.</summary>
    internal RecoveryService(ISessionStore store, TimeProvider? time, Func<string, Stream>? open, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _time = time ?? TimeProvider.System;
        _open = open ?? (static path => WavRepair.OpenForRepair(path));
        _delay = delay ?? ((span, token) => Task.Delay(span, _time, token));
    }

    /// <summary>
    /// Sessions skipped because a track file stayed busy after all retries, as of the last
    /// <see cref="RecoverAsync"/> or background pass. They are still <see cref="SessionState.Recording"/> in the store.
    /// </summary>
    public IReadOnlyList<SessionId> BusySessions => Volatile.Read(ref _busySessions);

    /// <returns>The sessions that were updated, as stored (busy sessions are not included, see <see cref="BusySessions"/>).</returns>
    public async ValueTask<IReadOnlyList<SessionRecord>> RecoverAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sessions = await _store.ListSessionsAsync(new SessionQuery(Limit: int.MaxValue), cancellationToken).ConfigureAwait(false);
            var started = _time.GetTimestamp();
            var recovered = new List<SessionRecord>();
            var busy = new List<SessionId>();

            foreach (var session in sessions)
            {
                if (session.State != SessionState.Recording)
                {
                    continue;
                }

                var updated = await RecoverSessionAsync(session, started, cancellationToken).ConfigureAwait(false);
                if (updated is null)
                {
                    busy.Add(session.Id);
                }
                else
                {
                    recovered.Add(updated);
                }
            }

            Volatile.Write(ref _busySessions, busy.ToArray());
            return recovered;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tries once more the sessions in <see cref="BusySessions"/> (and only those: any other session in the
    /// Recording state may be the live one). A session that is no longer Recording in the store is dropped.
    /// </summary>
    /// <returns>The sessions that were recovered by this pass; <see cref="BusySessions"/> keeps the ones still busy.</returns>
    public async ValueTask<IReadOnlyList<SessionRecord>> RetryBusyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var started = _time.GetTimestamp();
            var recovered = new List<SessionRecord>();
            var stillBusy = new List<SessionId>();

            foreach (var id in BusySessions)
            {
                var session = await _store.GetSessionAsync(id, cancellationToken).ConfigureAwait(false);
                if (session is null || session.State != SessionState.Recording)
                {
                    continue;
                }

                var updated = await RecoverSessionAsync(session, started, cancellationToken).ConfigureAwait(false);
                if (updated is null)
                {
                    stillBusy.Add(id);
                }
                else
                {
                    recovered.Add(updated);
                }
            }

            Volatile.Write(ref _busySessions, stillBusy.ToArray());
            return recovered;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs <see cref="RetryBusyAsync"/> after 30 s, 2 min and 10 min while <see cref="BusySessions"/> is not empty.
    /// Meant to be started without awaiting after <see cref="RecoverAsync"/>. A failing pass (e.g. a store error) is
    /// skipped and the next one still runs; only cancellation ends it early.
    /// </summary>
    public async Task RetryBusyInBackgroundAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < BackgroundSchedule.Length && BusySessions.Count > 0; i++)
        {
            await _delay(BackgroundSchedule[i], cancellationToken).ConfigureAwait(false);
            try
            {
                await RetryBusyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best effort: whatever is still busy stays Recording and is found again at the next start.
            }
        }
    }

    /// <returns>The stored update, or <c>null</c> when a track file is still busy (nothing was written).</returns>
    private async ValueTask<SessionRecord?> RecoverSessionAsync(SessionRecord session, long started, CancellationToken cancellationToken)
    {
        var files = _store.GetFiles(session.Id);
        var (micResult, micDuration) = await RepairWithRetryAsync(files.MicrophoneAudio, started, retry: true, cancellationToken).ConfigureAwait(false);

        // The session is skipped anyway when the first track is busy: give the second one a single look, no waiting.
        var (systemResult, systemDuration) = await RepairWithRetryAsync(
            files.SystemAudio, started, retry: micResult != WavRepairResult.Busy, cancellationToken).ConfigureAwait(false);

        if (micResult == WavRepairResult.Busy || systemResult == WavRepairResult.Busy)
        {
            return null;
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
        return updated;
    }

    private async ValueTask<(WavRepairResult Result, TimeSpan Duration)> RepairWithRetryAsync(
        string path, long started, bool retry, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = WavRepair.Repair(path, _open, out var duration);
            if (result != WavRepairResult.Busy || !retry || attempt >= Backoff.Length)
            {
                return (result, duration);
            }

            var remaining = RetryBudget - _time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return (result, duration);
            }

            var wait = Backoff[attempt];
            await _delay(wait < remaining ? wait : remaining, cancellationToken).ConfigureAwait(false);
        }
    }
}
