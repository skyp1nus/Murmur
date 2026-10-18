using System.Collections.Concurrent;

namespace Murmur.Core.Jobs;

/// <summary>Lower value runs first.</summary>
public enum JobPriority
{
    Transcription = 0,
    Indexing = 1,
    Summary = 2,
}

public enum JobState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <param name="CurrentJob">Name of the running job, or <c>null</c>.</param>
public readonly record struct JobQueueStatus(int Pending, int Running, string? CurrentJob, bool Paused);

/// <summary>One queued piece of work. <see cref="Completion"/> never faults: the outcome is in <see cref="State"/>/<see cref="Error"/>.</summary>
public sealed class JobHandle
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile JobState _state;

    internal JobHandle(long sequence, JobPriority priority, string name, Func<CancellationToken, Task> work)
    {
        Sequence = sequence;
        Priority = priority;
        Name = name;
        Work = work;
    }

    public JobPriority Priority { get; }

    public string Name { get; }

    public JobState State => _state;

    /// <summary>The exception the job threw, when <see cref="State"/> is <see cref="JobState.Failed"/>.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Completes (successfully) when the job succeeded, failed or was cancelled.</summary>
    public Task Completion => _completion.Task;

    /// <summary>How many times the job was started (a pause pre-empts a running job and restarts it later).</summary>
    public int Attempts { get; internal set; }

    internal long Sequence { get; }

    internal Func<CancellationToken, Task> Work { get; }

    internal void SetRunning() => _state = JobState.Running;

    internal void SetPending() => _state = JobState.Pending;

    internal void Finish(JobState state, Exception? error)
    {
        Error = error;
        _state = state;
        _completion.TrySetResult();
    }
}

/// <summary>
/// Background work queue: a single consumer runs one job at a time, highest <see cref="JobPriority"/> first and FIFO
/// within a priority.
/// </summary>
/// <remarks>
/// <para><b>Pausing.</b> Heavy work never overlaps a recording: <see cref="Pause"/> stops new jobs from starting and
/// pre-empts the running one (its token is cancelled; once it returns it goes back to the head of its priority and
/// runs again from the start after <see cref="Resume"/>), so jobs must be restartable. Pauses nest: the queue runs
/// again after as many <see cref="Resume"/> calls as <see cref="Pause"/> calls.</para>
/// <para><b>Errors.</b> A job that throws is <see cref="JobState.Failed"/> with <see cref="JobHandle.Error"/>; the queue
/// carries on. <see cref="DisposeAsync"/> cancels the running job and every pending one.</para>
/// <para><b>Status.</b> <see cref="StatusChanged"/> is raised on a thread-pool thread, never on the consumer, one event
/// at a time and in order; handlers must marshal to the UI thread themselves.</para>
/// </remarks>
public sealed class JobQueue : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly PriorityQueue<JobHandle, (int Priority, long Sequence)> _pending = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ConcurrentQueue<JobQueueStatus> _statusEvents = new();
    private readonly Task _consumer;

    private long _sequence;
    private int _pauseCount;
    private JobHandle? _current;
    private CancellationTokenSource? _currentCts;
    private bool _preempted;
    private bool _disposed;
    private int _dispatching;

    public JobQueue()
    {
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>Raised off the consumer thread after every change (enqueue, start, finish, pause, resume).</summary>
    public event Action<JobQueueStatus>? StatusChanged;

    public JobQueueStatus Status
    {
        get
        {
            lock (_gate)
            {
                return Snapshot();
            }
        }
    }

    public bool Paused
    {
        get
        {
            lock (_gate)
            {
                return _pauseCount > 0;
            }
        }
    }

    /// <exception cref="ObjectDisposedException">The queue was disposed.</exception>
    public JobHandle Enqueue(JobPriority priority, string name, Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(work);
        JobHandle job;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            job = new JobHandle(_sequence++, priority, name, work);
            _pending.Enqueue(job, ((int)priority, job.Sequence));
            PublishLocked();
        }

        _wake.Release();
        return job;
    }

    /// <summary>Stops starting jobs and pre-empts the running one (it is restarted after <see cref="Resume"/>).</summary>
    public void Pause()
    {
        CancellationTokenSource? preempt = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pauseCount++ == 0)
            {
                if (_currentCts is { } running)
                {
                    _preempted = true;
                    preempt = running;
                }

                PublishLocked();
            }
        }

        if (preempt is not null)
        {
            try
            {
                // Callbacks run on the thread pool: the caller (e.g. a recorder event) is never blocked by job code.
                _ = preempt.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // The job finished in the meantime.
            }
        }
    }

    /// <summary>Undoes one <see cref="Pause"/>; extra calls are ignored.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_disposed || _pauseCount == 0)
            {
                return;
            }

            if (--_pauseCount > 0)
            {
                return;
            }

            PublishLocked();
        }

        _wake.Release();
    }

    /// <summary>Cancels the running job and every pending job and stops the consumer.</summary>
    public async ValueTask DisposeAsync()
    {
        List<JobHandle> cancelled;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancelled = [];
            while (_pending.TryDequeue(out var job, out _))
            {
                cancelled.Add(job);
            }

            PublishLocked();
        }

        await _disposeCts.CancelAsync().ConfigureAwait(false);
        foreach (var job in cancelled)
        {
            job.Finish(JobState.Cancelled, null);
        }

        await _consumer.ConfigureAwait(false);
        _disposeCts.Dispose();
    }

    private async Task ConsumeAsync()
    {
        var disposing = _disposeCts.Token;
        while (true)
        {
            JobHandle? job = null;
            CancellationTokenSource? cts = null;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_pauseCount == 0 && _pending.TryDequeue(out job, out _))
                {
                    cts = CancellationTokenSource.CreateLinkedTokenSource(disposing);
                    _current = job;
                    _currentCts = cts;
                    _preempted = false;
                    job.Attempts++;
                    job.SetRunning();
                    PublishLocked();
                }
            }

            if (job is null)
            {
                try
                {
                    await _wake.WaitAsync(disposing).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            Exception? error = null;
            try
            {
                await job.Work(cts!.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            lock (_gate)
            {
                _current = null;
                _currentCts = null;
                // A job that completed although it was pre-empted keeps its result.
                var preempted = _preempted && !_disposed && error is not null;
                _preempted = false;
                if (preempted)
                {
                    // Paused while running: back to the head of its priority, run again after Resume.
                    job.SetPending();
                    _pending.Enqueue(job, ((int)job.Priority, job.Sequence));
                }
                else if (error is OperationCanceledException && cts!.IsCancellationRequested)
                {
                    job.Finish(JobState.Cancelled, null);
                }
                else
                {
                    job.Finish(error is null ? JobState.Succeeded : JobState.Failed, error);
                }

                PublishLocked();
            }

            cts!.Dispose();
        }
    }

    private JobQueueStatus Snapshot() =>
        new(_pending.Count, _current is null ? 0 : 1, _current?.Name, _pauseCount > 0);

    private void PublishLocked()
    {
        _statusEvents.Enqueue(Snapshot());
        if (Interlocked.CompareExchange(ref _dispatching, 1, 0) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static queue => queue.DispatchStatus(), this, preferLocal: false);
        }
    }

    private void DispatchStatus()
    {
        while (true)
        {
            while (_statusEvents.TryDequeue(out var status))
            {
                try
                {
                    StatusChanged?.Invoke(status);
                }
                catch (Exception)
                {
                    // A faulty subscriber must not stop the notifications.
                }
            }

            Volatile.Write(ref _dispatching, 0);
            if (_statusEvents.IsEmpty || Interlocked.CompareExchange(ref _dispatching, 1, 0) != 0)
            {
                return;
            }
        }
    }
}
