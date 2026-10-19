using Murmur.Desktop;

namespace Murmur.Desktop.Tests;

public sealed class AiReindexSchedulerTests
{
    private readonly object _gate = new();
    private int _runs;
    private int _concurrent;
    private int _maxConcurrent;
    private volatile bool _recording;
    private TaskCompletionSource? _block;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AiReindexScheduler Create(Action<Exception>? onError = null, Func<CancellationToken, Task>? work = null) =>
        new(() => _recording, work ?? RunAsync, onError);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _runs++;
            _concurrent++;
            _maxConcurrent = Math.Max(_maxConcurrent, _concurrent);
        }

        try
        {
            if (_block is { } block)
            {
                await block.Task.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            lock (_gate)
            {
                _concurrent--;
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task Request_RunsTheWorkOnce()
    {
        using var scheduler = Create();

        scheduler.Request();

        await WaitUntilAsync(() => Volatile.Read(ref _runs) == 1);
        await Task.Delay(100, Ct);
        Assert.Equal(1, Volatile.Read(ref _runs));
    }

    [Fact]
    public async Task RequestsDuringARun_NeverOverlap_AndCoalesceIntoOneMoreRun()
    {
        _block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = Create();

        scheduler.Request();
        await WaitUntilAsync(() => Volatile.Read(ref _runs) == 1);
        scheduler.Request();
        scheduler.Request();
        scheduler.Request();
        _block.SetResult();

        await WaitUntilAsync(() => Volatile.Read(ref _runs) == 2);
        await Task.Delay(100, Ct);
        Assert.Equal(2, Volatile.Read(ref _runs));
        Assert.Equal(1, _maxConcurrent);
    }

    [Fact]
    public async Task DuringARecording_NothingRuns_UntilItStops()
    {
        using var scheduler = Create();
        _recording = true;

        scheduler.Request();
        await Task.Delay(100, Ct);
        Assert.Equal(0, Volatile.Read(ref _runs));

        _recording = false;
        scheduler.NotifyRecordingStopped();

        await WaitUntilAsync(() => Volatile.Read(ref _runs) == 1);
    }

    [Fact]
    public async Task NotifyRecordingStopped_WithoutARequest_DoesNothing()
    {
        using var scheduler = Create();

        scheduler.NotifyRecordingStopped();

        await Task.Delay(100, Ct);
        Assert.Equal(0, Volatile.Read(ref _runs));
    }

    [Fact]
    public async Task AFailure_IsReported_AndTheSchedulerKeepsWorking()
    {
        var errors = new List<Exception>();
        var calls = 0;
        using var scheduler = Create(
            errors.Add,
            _ => ++calls == 1 ? throw new IOException("boom") : Task.CompletedTask);

        scheduler.Request();
        await WaitUntilAsync(() => errors.Count == 1);
        scheduler.Request();

        await WaitUntilAsync(() => Volatile.Read(ref calls) == 2);
        Assert.IsType<IOException>(errors[0]);
    }
}
