using Murmur.Core.Jobs;

namespace Murmur.Core.Tests.Jobs;

public sealed class JobQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Jobs_RunOneAtATime_ByPriorityThenFifo()
    {
        await using var queue = new JobQueue();
        var order = new List<string>();
        var running = 0;
        var maxRunning = 0;
        queue.Pause(); // queue everything first so the order is decided by priority alone

        Func<CancellationToken, Task> Work(string name) => async ct =>
        {
            var now = Interlocked.Increment(ref running);
            lock (order)
            {
                maxRunning = Math.Max(maxRunning, now);
                order.Add(name);
            }

            await Task.Delay(5, ct);
            Interlocked.Decrement(ref running);
        };

        var jobs = new[]
        {
            queue.Enqueue(JobPriority.Summary, "summary-1", Work("summary-1")),
            queue.Enqueue(JobPriority.Indexing, "index-1", Work("index-1")),
            queue.Enqueue(JobPriority.Transcription, "transcribe-1", Work("transcribe-1")),
            queue.Enqueue(JobPriority.Indexing, "index-2", Work("index-2")),
            queue.Enqueue(JobPriority.Transcription, "transcribe-2", Work("transcribe-2")),
        };
        Assert.Equal(new JobQueueStatus(5, 0, null, true), queue.Status);

        queue.Resume();
        await Task.WhenAll(jobs.Select(j => j.Completion)).WaitAsync(Timeout, Ct);

        Assert.Equal(["transcribe-1", "transcribe-2", "index-1", "index-2", "summary-1"], order);
        Assert.Equal(1, maxRunning);
        Assert.All(jobs, j => Assert.Equal(JobState.Succeeded, j.State));
        Assert.Equal(new JobQueueStatus(0, 0, null, false), queue.Status);
    }

    [Fact]
    public async Task HigherPriority_EnqueuedWhileRunning_GoesNext()
    {
        await using var queue = new JobQueue();
        var release = new TaskCompletionSource();
        var order = new List<string>();
        var first = queue.Enqueue(JobPriority.Summary, "first", async _ =>
        {
            order.Add("first");
            await release.Task;
        });
        await WaitUntilAsync(() => queue.Status.Running == 1);

        var summary = queue.Enqueue(JobPriority.Summary, "summary", _ => { order.Add("summary"); return Task.CompletedTask; });
        var transcription = queue.Enqueue(JobPriority.Transcription, "transcription", _ => { order.Add("transcription"); return Task.CompletedTask; });
        Assert.Equal(new JobQueueStatus(2, 1, "first", false), queue.Status);
        release.SetResult();

        await Task.WhenAll(first.Completion, summary.Completion, transcription.Completion).WaitAsync(Timeout, Ct);
        Assert.Equal(["first", "transcription", "summary"], order);
    }

    [Fact]
    public async Task Exceptions_AreCapturedPerJob_AndTheQueueCarriesOn()
    {
        await using var queue = new JobQueue();
        var failing = queue.Enqueue(JobPriority.Transcription, "fails", _ => throw new InvalidOperationException("boom"));
        var asyncFailing = queue.Enqueue(JobPriority.Transcription, "fails-async", async _ =>
        {
            await Task.Yield();
            throw new IOException("disk");
        });
        var ok = queue.Enqueue(JobPriority.Transcription, "ok", _ => Task.CompletedTask);

        await Task.WhenAll(failing.Completion, asyncFailing.Completion, ok.Completion).WaitAsync(Timeout, Ct);

        Assert.Equal(JobState.Failed, failing.State);
        Assert.Equal("boom", Assert.IsType<InvalidOperationException>(failing.Error).Message);
        Assert.Equal(JobState.Failed, asyncFailing.State);
        Assert.IsType<IOException>(asyncFailing.Error);
        Assert.Equal(JobState.Succeeded, ok.State);
        Assert.Null(ok.Error);
    }

    [Fact]
    public async Task Pause_HoldsPendingJobs_UntilResume()
    {
        await using var queue = new JobQueue();
        queue.Pause();
        var ran = false;
        var job = queue.Enqueue(JobPriority.Transcription, "held", _ => { ran = true; return Task.CompletedTask; });

        await Task.Delay(100, Ct);
        Assert.False(ran);
        Assert.Equal(JobState.Pending, job.State);
        Assert.True(queue.Paused);

        queue.Resume();
        await job.Completion.WaitAsync(Timeout, Ct);
        Assert.True(ran);
        Assert.False(queue.Paused);
    }

    [Fact]
    public async Task Pause_PreemptsTheRunningJob_AndRestartsItAfterResume()
    {
        await using var queue = new JobQueue();
        var started = new SemaphoreSlim(0);
        var cancellations = 0;
        var job = queue.Enqueue(JobPriority.Transcription, "long", async ct =>
        {
            started.Release();
            try
            {
                await Task.Delay(JobDelay(), ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancellations);
                throw;
            }
        });
        var later = queue.Enqueue(JobPriority.Indexing, "later", _ => Task.CompletedTask);

        Assert.True(await started.WaitAsync(Timeout, Ct));
        queue.Pause();
        await WaitUntilAsync(() => queue.Status is { Running: 0, Pending: 2 });
        Assert.Equal(1, cancellations);
        Assert.Equal(JobState.Pending, job.State);
        Assert.Equal(JobState.Pending, later.State);

        _fast = true;
        queue.Resume();
        await Task.WhenAll(job.Completion, later.Completion).WaitAsync(Timeout, Ct);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(2, job.Attempts);
        Assert.Equal(JobState.Succeeded, later.State);
    }

    [Fact]
    public async Task Pause_Nests()
    {
        await using var queue = new JobQueue();
        queue.Pause();
        queue.Pause();
        var job = queue.Enqueue(JobPriority.Transcription, "held", _ => Task.CompletedTask);

        queue.Resume();
        await Task.Delay(50, Ct);
        Assert.Equal(JobState.Pending, job.State);

        queue.Resume();
        queue.Resume(); // extra resume is ignored
        await job.Completion.WaitAsync(Timeout, Ct);
        Assert.Equal(JobState.Succeeded, job.State);

        queue.Pause();
        Assert.True(queue.Paused);
    }

    [Fact]
    public async Task Dispose_CancelsRunningAndPendingJobs()
    {
        var queue = new JobQueue();
        var started = new TaskCompletionSource();
        var running = queue.Enqueue(JobPriority.Transcription, "running", async ct =>
        {
            started.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
        });
        var pending = queue.Enqueue(JobPriority.Summary, "pending", _ => Task.CompletedTask);
        await started.Task.WaitAsync(Timeout, Ct);

        await queue.DisposeAsync().AsTask().WaitAsync(Timeout, Ct);
        await queue.DisposeAsync();

        Assert.Equal(JobState.Cancelled, running.State);
        Assert.Equal(JobState.Cancelled, pending.State);
        Assert.True(running.Completion.IsCompletedSuccessfully);
        Assert.Throws<ObjectDisposedException>(() => queue.Enqueue(JobPriority.Summary, "late", _ => Task.CompletedTask));
        Assert.Throws<ObjectDisposedException>(queue.Pause);
        queue.Resume(); // harmless after dispose
    }

    [Fact]
    public async Task StatusChanged_IsRaisedInOrder()
    {
        await using var queue = new JobQueue();
        var statuses = new List<JobQueueStatus>();
        var done = new TaskCompletionSource();
        queue.StatusChanged += s =>
        {
            lock (statuses)
            {
                statuses.Add(s);
                if (s is { Pending: 0, Running: 0 } && statuses.Count >= 3)
                {
                    done.TrySetResult();
                }
            }
        };

        var job = queue.Enqueue(JobPriority.Transcription, "named", _ =>
        {
            Thread.Sleep(20);
            return Task.CompletedTask;
        });
        await job.Completion.WaitAsync(Timeout, Ct);
        await done.Task.WaitAsync(Timeout, Ct);

        lock (statuses)
        {
            Assert.Equal(new JobQueueStatus(1, 0, null, false), statuses[0]);
            Assert.Equal(new JobQueueStatus(0, 1, "named", false), statuses[1]);
            Assert.Equal(new JobQueueStatus(0, 0, null, false), statuses[2]);
        }
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotStopTheQueue()
    {
        await using var queue = new JobQueue();
        queue.StatusChanged += _ => throw new InvalidOperationException("bad subscriber");

        var job = queue.Enqueue(JobPriority.Transcription, "ok", _ => Task.CompletedTask);

        await job.Completion.WaitAsync(Timeout, Ct);
        Assert.Equal(JobState.Succeeded, job.State);
    }

    [Fact]
    public async Task Enqueue_ValidatesArguments()
    {
        await using var queue = new JobQueue();

        Assert.Throws<ArgumentNullException>(() => queue.Enqueue(JobPriority.Summary, null!, _ => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => queue.Enqueue(JobPriority.Summary, "x", null!));
    }

    private volatile bool _fast;

    private int JobDelay() => _fast ? 1 : System.Threading.Timeout.Infinite;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(5, Ct);
        }
    }
}
