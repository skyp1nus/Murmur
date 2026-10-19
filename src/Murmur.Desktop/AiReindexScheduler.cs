namespace Murmur.Desktop;

/// <summary>
/// Runs "index the sessions that have no vectors yet" when the AI becomes available while the app is running.
/// Requests coalesce: at most one run at a time, and a request made during a run triggers exactly one more run after it.
/// Nothing runs during a recording; the request is kept and run when the recording stops.
/// </summary>
internal sealed class AiReindexScheduler : IDisposable
{
    private readonly Func<bool> _isRecording;
    private readonly Func<CancellationToken, Task> _reindex;
    private readonly Action<Exception>? _onError;
    private readonly CancellationTokenSource _cts = new();
    private int _requested;
    private int _running;

    /// <param name="isRecording">Whether a recording is in progress.</param>
    /// <param name="reindex">The work (refreshes the AI state, then queues the indexing jobs).</param>
    /// <param name="onError">Called with a failure of <paramref name="reindex"/>; the scheduler stays usable.</param>
    public AiReindexScheduler(Func<bool> isRecording, Func<CancellationToken, Task> reindex, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(isRecording);
        ArgumentNullException.ThrowIfNull(reindex);
        _isRecording = isRecording;
        _reindex = reindex;
        _onError = onError;
    }

    /// <summary>Asks for a run; returns immediately (the work happens on the thread pool).</summary>
    public void Request()
    {
        Volatile.Write(ref _requested, 1);
        Kick();
    }

    /// <summary>Call when a recording has ended: runs a request that had to wait for it.</summary>
    public void NotifyRecordingStopped() => Kick();

    public void Dispose() => _cts.Cancel();

    private void Kick()
    {
        if (Volatile.Read(ref _requested) == 0 || _isRecording() || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested && !_isRecording() && Interlocked.Exchange(ref _requested, 0) == 1)
            {
                await _reindex(_cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }

        // A request that arrived after the loop looked, but before _running was cleared, was refused by Kick.
        if (!_cts.IsCancellationRequested)
        {
            Kick();
        }
    }
}
