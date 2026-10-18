using System.Diagnostics;
using Murmur.Core.Tests.Fakes;
using Murmur.Core.Transcription;

namespace Murmur.Core.Tests.Transcription;

/// <summary><see cref="WorkerAsrClient"/> against <c>Murmur.Core.FakeAsrWorker</c> (run with <c>dotnet</c>).</summary>
public sealed class WorkerAsrClientTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _dir = new();
    private readonly ModelLocator _models;

    public WorkerAsrClientTests()
    {
        _models = ModelLocatorTests.Install(_dir.File("models"));
        Configure("mode=normal");
    }

    public static string FakeWorkerDll => Path.Combine(AppContext.BaseDirectory, "Murmur.Core.FakeAsrWorker.dll");

    private string WorkerLog => Path.Combine(_models.ParakeetDirectory, "fake-worker.log");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Transcribe_StreamsSegmentsWithSpeakerTimesWordsAndProgress()
    {
        await using var client = CreateClient(threads: 2);
        var wav = TestWav.Silence(_dir.File("system.wav"), TimeSpan.FromSeconds(3));
        var progress = new ListProgress();
        Assert.Equal(AsrStatus.Idle, client.Status);

        var segments = new List<TranscriptSegment>();
        var busySeen = false;
        await foreach (var s in client.TranscribeAsync(new AsrRequest(wav, Speaker.Others), progress, CancellationToken.None).WithCancellation(CancellationToken.None))
        {
            busySeen |= client.Status == AsrStatus.Busy;
            segments.Add(s);
        }

        Assert.True(busySeen);
        Assert.Equal(AsrStatus.Idle, client.Status);
        Assert.Equal(3, segments.Count);
        Assert.All(segments, s => Assert.Equal(Speaker.Others, s.Speaker));
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], segments.Select(s => s.Start));
        Assert.Equal(TimeSpan.FromSeconds(3), segments[^1].End);
        Assert.Equal("system 1", segments[1].Text);
        Assert.Equal([new TranscriptWord("system", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5)), new TranscriptWord("1", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2))], segments[1].Words);
        Assert.Equal(new AsrProgress(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)), progress.Values[^1]);
        Assert.Contains(new AsrProgress(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)), progress.Values);

        var start = Assert.Single(Log(), l => l.StartsWith("start ", StringComparison.Ordinal));
        Assert.Equal($"start --serve --model-dir {_models.ParakeetDirectory} --vad {_models.VadModel} --threads 2 --idle-exit-seconds 120", start);
        Assert.Contains(Log(), l => l.Contains("\"kind\":\"transcribe\"", StringComparison.Ordinal) && l.Contains("\"audioPath\":", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Requests_ReuseOneWorker_AndAreSerialized()
    {
        Configure("mode=normal", "segments=4", "delayMs=50");
        await using var client = CreateClient();
        var a = TestWav.Silence(_dir.File("a.wav"), TimeSpan.FromSeconds(1));
        var b = TestWav.Silence(_dir.File("b.wav"), TimeSpan.FromSeconds(1));
        var order = new List<string>();

        async Task Run(string path, Speaker speaker)
        {
            await foreach (var s in client.TranscribeAsync(new AsrRequest(path, speaker), null, CancellationToken.None))
            {
                lock (order)
                {
                    order.Add(s.Text);
                }
            }
        }

        await Task.WhenAll(Run(a, Speaker.Me), Run(b, Speaker.Others)).WaitAsync(Timeout, Ct);

        Assert.Equal(1, client.WorkerStarts);
        Assert.Equal(8, order.Count);
        var first = order[0].Split(' ')[0];
        Assert.All(order.Take(4), t => Assert.StartsWith(first, t, StringComparison.Ordinal)); // no interleaving
        Assert.True(client.IsWorkerRunning);
    }

    [Fact]
    public async Task ErrorEvent_FailsTheJobButKeepsTheWorker()
    {
        Configure("mode=error");
        await using var client = CreateClient();
        var wav = TestWav.Silence(_dir.File("a.wav"), TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<AsrWorkerException>(() => DrainAsync(client, wav));
        Assert.Contains("simulated failure", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<AsrWorkerException>(() => DrainAsync(client, wav));

        Assert.Equal(1, client.WorkerStarts);
        Assert.True(client.IsWorkerRunning);
        Assert.Equal(AsrStatus.Idle, client.Status);
    }

    [Fact]
    public async Task CrashMidJob_ThrowsWithTheStderrTail_AndTheNextRequestStartsAFreshWorker()
    {
        Configure("mode=crash");
        await using var client = CreateClient();
        var wav = TestWav.Silence(_dir.File("a.wav"), TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<AsrWorkerException>(() => DrainAsync(client, wav));

        Assert.Equal(3, ex.ExitCode);
        Assert.Contains("simulated crash in the decoder", ex.Message, StringComparison.Ordinal);
        Assert.Contains("simulated crash in the decoder", ex.StderrTail, StringComparison.Ordinal);
        Assert.False(client.IsWorkerRunning);

        Configure("mode=normal");
        Assert.Equal(3, await DrainAsync(client, wav));
        Assert.Equal(2, client.WorkerStarts);
    }

    [Fact]
    public async Task CrashOnStart_ReportsTheStderrTail()
    {
        Configure("mode=crash-on-start");
        await using var client = CreateClient();
        var wav = TestWav.Silence(_dir.File("a.wav"), TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<AsrWorkerException>(() => DrainAsync(client, wav));

        Assert.Equal(4, ex.ExitCode);
        Assert.Contains("cannot load the model", ex.Message, StringComparison.Ordinal);
        Assert.Equal(AsrStatus.Idle, client.Status);
    }

    [Fact]
    public async Task WorkerLevelError_FailsTheJob()
    {
        Configure("mode=fatal");
        await using var client = CreateClient();
        var wav = TestWav.Silence(_dir.File("a.wav"), TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<AsrWorkerException>(() => DrainAsync(client, wav));

        Assert.Contains("internal error: boom", ex.Message, StringComparison.Ordinal);
        Assert.False(client.IsWorkerRunning);
    }

    [Fact]
    public async Task NoReady_TimesOut()
    {
        Configure("mode=no-ready");
        await using var client = CreateClient(startTimeout: TimeSpan.FromMilliseconds(700));
        var wav = TestWav.Silence(_dir.File("a.wav"), TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<AsrWorkerException>(() => DrainAsync(client, wav));

        Assert.Contains("did not report ready", ex.Message, StringComparison.Ordinal);
        Assert.False(client.IsWorkerRunning);
    }

    [Fact]
    public async Task Cancel_SendsCancel_AndTheWorkerStaysAlive()
    {
        Configure("mode=normal", "segments=100", "delayMs=100");
        await using var client = CreateClient();
        var wav = TestWav.Silence(_dir.File("a.wav"), TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource();
        var received = 0;
        var sw = new Stopwatch();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.TranscribeAsync(new AsrRequest(wav, Speaker.Me), null, cts.Token))
            {
                if (++received == 1)
                {
                    sw.Start();
                    await cts.CancelAsync();
                }
            }
        });

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"cancel took {sw.Elapsed}");
        Assert.Contains(Log(), l => l.Contains("\"kind\":\"cancel\"", StringComparison.Ordinal));
        Assert.True(client.IsWorkerRunning);
        Assert.Equal(AsrStatus.Idle, client.Status);
        Assert.Equal(1, client.WorkerStarts);
    }

    [Fact]
    public async Task Cancel_KillsAWorkerThatDoesNotAnswer()
    {
        Configure("mode=hang");
        await using var client = CreateClient(cancelTimeout: TimeSpan.FromMilliseconds(500));
        var wav = TestWav.Silence(_dir.File("a.wav"), TimeSpan.FromSeconds(1));
        using var cts = new CancellationTokenSource();
        var sw = new Stopwatch();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.TranscribeAsync(new AsrRequest(wav, Speaker.Me), null, cts.Token))
            {
                sw.Start();
                cts.CancelAfter(50);
            }
        });

        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10));
        Assert.False(client.IsWorkerRunning);

        Configure("mode=normal");
        Assert.Equal(3, await DrainAsync(client, wav));
        Assert.Equal(2, client.WorkerStarts);
    }

    [Fact]
    public async Task AbandonedEnumeration_CancelsTheJob()
    {
        Configure("mode=normal", "segments=100", "delayMs=50");
        await using var client = CreateClient();
        var wav = TestWav.Silence(_dir.File("a.wav"), TimeSpan.FromSeconds(10));

        await foreach (var _ in client.TranscribeAsync(new AsrRequest(wav, Speaker.Me), null, CancellationToken.None))
        {
            break;
        }

        Assert.Contains(Log(), l => l.Contains("\"kind\":\"cancel\"", StringComparison.Ordinal));
        Assert.Equal(AsrStatus.Idle, client.Status);
        Assert.True(client.IsWorkerRunning);
    }

    [Fact]
    public async Task ModelMissing_IsReportedAndRefused()
    {
        await using var client = new WorkerAsrClient(new ModelLocator(_dir.File("no-models")), new WorkerAsrClientOptions(WorkerPath: FakeWorkerDll));

        Assert.Equal(AsrStatus.ModelMissing, client.Status);
        var ex = await Assert.ThrowsAsync<AsrModelMissingException>(() => DrainAsync(client, _dir.File("a.wav")));
        Assert.Equal(5, ex.MissingFiles.Count);
        Assert.Equal(0, client.WorkerStarts);
    }

    [Fact]
    public async Task MissingWorker_IsAClearError()
    {
        await using var client = new WorkerAsrClient(_models, new WorkerAsrClientOptions(WorkerPath: _dir.File("Murmur.Transcription")));
        var wav = TestWav.Silence(_dir.File("a.wav"), TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<AsrWorkerException>(() => DrainAsync(client, wav));

        Assert.Contains("not found", ex.Message, StringComparison.Ordinal);
        Assert.Contains(WorkerAsrClient.WorkerPathVariable, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdleExitedWorker_IsReplacedOnTheNextRequest()
    {
        await using var client = CreateClient(idleExitSeconds: 1);
        var wav = TestWav.Silence(_dir.File("a.wav"), TimeSpan.FromSeconds(1));
        Assert.Equal(3, await DrainAsync(client, wav));

        var deadline = DateTime.UtcNow + Timeout;
        while (client.IsWorkerRunning)
        {
            Assert.True(DateTime.UtcNow < deadline, "the worker did not idle-exit");
            await Task.Delay(50, Ct);
        }

        Assert.Contains("idle exit", Log());
        Assert.Equal(3, await DrainAsync(client, wav));
        Assert.Equal(2, client.WorkerStarts);
    }

    [Fact]
    public async Task ShutdownWorker_StopsIt_AndTheNextRequestStartsAnother()
    {
        await using var client = CreateClient();
        var wav = TestWav.Silence(_dir.File("a.wav"), TimeSpan.FromSeconds(1));
        await DrainAsync(client, wav);
        Assert.True(client.IsWorkerRunning);

        await client.ShutdownWorkerAsync(Ct);

        Assert.False(client.IsWorkerRunning);
        Assert.Contains("shutdown", Log());
        await DrainAsync(client, wav);
        Assert.Equal(2, client.WorkerStarts);
    }

    [Fact]
    public async Task Dispose_ShutsTheWorkerDown_AndRejectsNewRequests()
    {
        var client = CreateClient();
        var wav = TestWav.Silence(_dir.File("a.wav"), TimeSpan.FromSeconds(1));
        await DrainAsync(client, wav);

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.False(client.IsWorkerRunning);
        Assert.Contains("shutdown", Log());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => DrainAsync(client, wav));
    }

    [Fact]
    public async Task Dispose_DuringAJob_CancelsIt()
    {
        Configure("mode=normal", "segments=100", "delayMs=100");
        var client = CreateClient();
        var wav = TestWav.Silence(_dir.File("a.wav"), TimeSpan.FromSeconds(10));
        var first = new TaskCompletionSource();
        var job = Task.Run(async () =>
        {
            await foreach (var _ in client.TranscribeAsync(new AsrRequest(wav, Speaker.Me), null, CancellationToken.None))
            {
                first.TrySetResult();
            }
        }, Ct);
        await first.Task.WaitAsync(Timeout, Ct);

        await client.DisposeAsync().AsTask().WaitAsync(Timeout, Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.WaitAsync(Timeout, Ct));
        Assert.False(client.IsWorkerRunning);
    }

    [Fact]
    public void ResolveWorkerPath_PrefersTheConfiguredPath()
    {
        Assert.Equal("/opt/worker", WorkerAsrClient.ResolveWorkerPath("/opt/worker"));

        var fromEnvironment = Environment.GetEnvironmentVariable(WorkerAsrClient.WorkerPathVariable);
        var expected = string.IsNullOrEmpty(fromEnvironment)
            ? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Murmur.Transcription.exe" : "Murmur.Transcription")
            : fromEnvironment;
        Assert.Equal(expected, WorkerAsrClient.ResolveWorkerPath());
    }

    [Fact]
    public async Task CommandLine_RunsADllWithDotnet_AndAnExeDirectly()
    {
        await using var dll = new WorkerAsrClient(_models, new WorkerAsrClientOptions(WorkerPath: "/w/Murmur.Transcription.dll", Threads: 3, IdleExitSeconds: 7));
        var (fileName, arguments) = dll.BuildCommandLine();
        Assert.Equal(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet", Path.GetFileName(fileName));
        Assert.Equal(["/w/Murmur.Transcription.dll", "--serve", "--model-dir", _models.ParakeetDirectory, "--vad", _models.VadModel, "--threads", "3", "--idle-exit-seconds", "7"], arguments);

        await using var exe = new WorkerAsrClient(_models, new WorkerAsrClientOptions(WorkerPath: "/w/Murmur.Transcription"));
        (fileName, arguments) = exe.BuildCommandLine();
        Assert.Equal("/w/Murmur.Transcription", fileName);
        Assert.Equal("--serve", arguments[0]);
        Assert.Equal(Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture), arguments[6]);
        Assert.Equal("120", arguments[8]);
    }

    private static async Task<int> DrainAsync(WorkerAsrClient client, string wav)
    {
        var count = 0;
        await foreach (var _ in client.TranscribeAsync(new AsrRequest(wav, Speaker.Me), null, CancellationToken.None).ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    private WorkerAsrClient CreateClient(int threads = 1, TimeSpan? cancelTimeout = null, TimeSpan? startTimeout = null, int idleExitSeconds = 120)
    {
        Assert.True(File.Exists(FakeWorkerDll), "fake worker not found: " + FakeWorkerDll);
        return new WorkerAsrClient(_models, new WorkerAsrClientOptions(FakeWorkerDll, threads, idleExitSeconds, cancelTimeout, startTimeout ?? Timeout));
    }

    private void Configure(params string[] lines) => File.WriteAllLines(Path.Combine(_models.ParakeetDirectory, "fake-worker.ini"), lines);

    private string[] Log() => File.Exists(WorkerLog) ? File.ReadAllLines(WorkerLog) : [];

    private sealed class ListProgress : IProgress<AsrProgress>
    {
        private readonly List<AsrProgress> _values = [];

        public List<AsrProgress> Values
        {
            get
            {
                lock (_values)
                {
                    return [.. _values];
                }
            }
        }

        public void Report(AsrProgress value)
        {
            lock (_values)
            {
                _values.Add(value);
            }
        }
    }
}
