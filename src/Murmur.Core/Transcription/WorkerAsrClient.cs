using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Murmur.Core.Transcription.Protocol;

namespace Murmur.Core.Transcription;

/// <param name="WorkerPath">Worker executable (or a <c>.dll</c>, run with <c>dotnet</c>); <c>null</c> = <see cref="WorkerAsrClient.ResolveWorkerPath"/>.</param>
/// <param name="Threads">CPU threads for the worker (<c>--threads</c>); 0 = logical cores.</param>
/// <param name="IdleExitSeconds">The worker exits (and frees the model) after this long without a job.</param>
/// <param name="CancelTimeout">How long a cancelled or shut-down worker may take to answer before it is killed.</param>
/// <param name="StartTimeout">How long the worker may take to report <c>ready</c>.</param>
public sealed record WorkerAsrClientOptions(
    string? WorkerPath = null,
    int Threads = 0,
    int IdleExitSeconds = 120,
    TimeSpan? CancelTimeout = null,
    TimeSpan? StartTimeout = null);

/// <summary>
/// <see cref="IAsr"/> backed by the <c>Murmur.Transcription</c> worker process (JSON lines, see
/// <see cref="WorkerRequest"/>/<see cref="WorkerEvent"/>).
/// </summary>
/// <remarks>
/// <para>The worker is started lazily on the first request (and again after it exited: idle timeout, crash, kill) and
/// waited on until it reports <c>ready</c>. Jobs run one at a time: a second <see cref="TranscribeAsync"/> waits for
/// the first to finish.</para>
/// <para>Cancellation (or abandoning the enumeration early) sends <c>cancel</c> and waits up to
/// <see cref="WorkerAsrClientOptions.CancelTimeout"/> (5 s) for the worker's terminal event, then kills it. A worker
/// that dies mid-job fails the job with an <see cref="AsrWorkerException"/> carrying the stderr tail; the next request
/// starts a fresh worker. An <c>error</c> event for the job fails it with <see cref="AsrWorkerException"/> but keeps
/// the worker.</para>
/// <para><see cref="ShutdownWorkerAsync"/> frees the model memory right away (e.g. before a recording starts).</para>
/// </remarks>
public sealed class WorkerAsrClient : IAsr, IAsyncDisposable
{
    public const string WorkerPathVariable = "MURMUR_TRANSCRIPTION_WORKER";

    private const string WorkerName = "Murmur.Transcription";

    private static readonly TimeSpan DefaultCancelTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(30);

    private readonly ModelLocator _models;
    private readonly string? _configuredWorkerPath;
    private readonly int _threads;
    private readonly int _idleExitSeconds;
    private readonly TimeSpan _cancelTimeout;
    private readonly TimeSpan _startTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    private WorkerConnection? _worker;
    private int _busy;
    private int _disposed;
    private int _workerStarts;

    public WorkerAsrClient(ModelLocator models, WorkerAsrClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        options ??= new WorkerAsrClientOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.Threads, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.IdleExitSeconds, nameof(options));

        _models = models;
        _configuredWorkerPath = options.WorkerPath;
        _threads = options.Threads > 0 ? options.Threads : Environment.ProcessorCount;
        _idleExitSeconds = options.IdleExitSeconds;
        _cancelTimeout = options.CancelTimeout ?? DefaultCancelTimeout;
        _startTimeout = options.StartTimeout ?? DefaultStartTimeout;
    }

    public AsrStatus Status => !_models.IsAsrInstalled ? AsrStatus.ModelMissing : Volatile.Read(ref _busy) != 0 ? AsrStatus.Busy : AsrStatus.Idle;

    /// <summary>The worker this client starts.</summary>
    public string WorkerPath => ResolveWorkerPath(_configuredWorkerPath);

    /// <summary>Worker processes started so far (diagnostics and tests).</summary>
    public int WorkerStarts => Volatile.Read(ref _workerStarts);

    /// <summary>Whether a worker process is currently running.</summary>
    public bool IsWorkerRunning => _worker is { HasExited: false };

    /// <summary>
    /// <paramref name="configured"/>, else <c>$MURMUR_TRANSCRIPTION_WORKER</c>, else <c>Murmur.Transcription(.exe)</c>
    /// next to the app (<see cref="AppContext.BaseDirectory"/>).
    /// </summary>
    public static string ResolveWorkerPath(string? configured = null)
    {
        if (!string.IsNullOrEmpty(configured))
        {
            return configured;
        }

        if (Environment.GetEnvironmentVariable(WorkerPathVariable) is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        return Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? WorkerName + ".exe" : WorkerName);
    }

    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(AsrRequest request, IProgress<AsrProgress>? progress, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_models.MissingAsrFiles() is { Count: > 0 } missing)
        {
            throw new AsrModelMissingException(missing);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var token = linked.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        Volatile.Write(ref _busy, 1);
        var job = new Job(request, progress, Guid.NewGuid().ToString("N"));
        try
        {
            await StartJobAsync(job, cancellationToken, token).ConfigureAwait(false);
            while (true)
            {
                var segment = await NextSegmentAsync(job, cancellationToken, token).ConfigureAwait(false);
                if (segment is null)
                {
                    yield break;
                }

                yield return segment;
            }
        }
        finally
        {
            if (!job.Finished)
            {
                await AbortAsync(job).ConfigureAwait(false);
            }

            Volatile.Write(ref _busy, 0);
            _gate.Release();
        }
    }

    /// <summary>Stops the worker (and so frees the model); waits for a running job to finish or be cancelled first.</summary>
    public async ValueTask ShutdownWorkerAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopWorkerAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _disposeCts.CancelAsync().ConfigureAwait(false);

        // A running job aborts (cancel + wait up to the cancel timeout); a consumer that stopped enumerating can hold
        // the gate forever, so do not wait for it longer than that.
        var acquired = await _gate.WaitAsync(_cancelTimeout + TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        try
        {
            await StopWorkerAsync().ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }
        }
    }

    /// <summary>The dotnet host for running a worker <c>.dll</c> (dev and tests).</summary>
    internal static string DotnetHost()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath))
        {
            return hostPath;
        }

        if (Environment.ProcessPath is { } processPath && string.Equals(Path.GetFileName(processPath), exe, StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, exe)))
        {
            return Path.Combine(root, exe);
        }

        // <root>/shared/Microsoft.NETCore.App/<version>/
        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (runtimeDirectory.Length > 0 && Directory.GetParent(runtimeDirectory)?.Parent?.Parent?.FullName is { } dotnetRoot && File.Exists(Path.Combine(dotnetRoot, exe)))
        {
            return Path.Combine(dotnetRoot, exe);
        }

        return exe;
    }

    /// <summary>The command line that starts the worker: (file name, arguments).</summary>
    internal (string FileName, List<string> Arguments) BuildCommandLine()
    {
        var path = WorkerPath;
        var arguments = new List<string>();
        string fileName;
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            fileName = DotnetHost();
            arguments.Add(path);
        }
        else
        {
            fileName = path;
        }

        arguments.AddRange(
        [
            "--serve",
            "--model-dir", _models.ParakeetDirectory,
            "--vad", _models.VadModel,
            "--threads", _threads.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--idle-exit-seconds", _idleExitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ]);
        return (fileName, arguments);
    }

    private static TranscriptSegment ToSegment(WorkerSegment segment, Speaker speaker)
    {
        var words = segment.Words is { Count: > 0 } source ? new TranscriptWord[source.Count] : [];
        for (var i = 0; i < words.Length; i++)
        {
            var w = segment.Words[i];
            words[i] = new TranscriptWord(w.Text ?? string.Empty, TimeSpan.FromSeconds(w.Start), TimeSpan.FromSeconds(w.End));
        }

        return new TranscriptSegment(speaker, TimeSpan.FromSeconds(segment.Start), TimeSpan.FromSeconds(segment.End), segment.Text ?? string.Empty, words);
    }

    private static string Describe(WorkerConnection worker)
    {
        var code = worker.ExitCode is { } c ? $" (exit code {c})" : string.Empty;
        var tail = worker.StderrTail;
        return tail.Length == 0 ? code : code + ". stderr:\n" + tail;
    }

    private async ValueTask StartJobAsync(Job job, CancellationToken callerToken, CancellationToken token)
    {
        var worker = await EnsureWorkerAsync(callerToken, token).ConfigureAwait(false);
        job.Worker = worker;
        try
        {
            await worker.SendAsync(
                new WorkerRequest { Kind = WorkerKinds.Transcribe, RequestId = job.Id, AudioPath = Path.GetFullPath(job.Request.AudioPath), Language = job.Request.Language },
                token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The worker died between start-up and the request; reading its events reports why.
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await AbortAsync(job).ConfigureAwait(false);
            callerToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private async ValueTask<TranscriptSegment?> NextSegmentAsync(Job job, CancellationToken callerToken, CancellationToken token)
    {
        while (true)
        {
            var worker = job.Worker!;
            WorkerEvent? e;
            try
            {
                e = await worker.ReadAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await AbortAsync(job).ConfigureAwait(false);
                callerToken.ThrowIfCancellationRequested();
                throw;
            }

            if (e is null)
            {
                await worker.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                // The worker idle-exited just as the request went out: nothing of this job happened yet, so retry once.
                if (!job.AnyEvent && !job.Retried && worker.ExitCode == 0)
                {
                    job.Retried = true;
                    await DropWorkerAsync(worker).ConfigureAwait(false);
                    await StartJobAsync(job, callerToken, token).ConfigureAwait(false);
                    continue;
                }

                job.Finished = true;
                var exception = new AsrWorkerException("The ASR worker exited unexpectedly" + Describe(worker), worker.ExitCode, worker.StderrTail);
                await DropWorkerAsync(worker).ConfigureAwait(false);
                throw exception;
            }

            if (e.Kind == WorkerKinds.Error && e.RequestId is null)
            {
                // A worker-level failure ("internal error"): the worker is going down.
                job.Finished = true;
                await worker.WaitForExitAsync(_cancelTimeout).ConfigureAwait(false);
                var exception = new AsrWorkerException("The ASR worker failed: " + e.Error + Describe(worker), worker.ExitCode, worker.StderrTail);
                await DropWorkerAsync(worker).ConfigureAwait(false);
                throw exception;
            }

            if (e.RequestId != job.Id)
            {
                continue;
            }

            job.AnyEvent = true;
            switch (e.Kind)
            {
                case WorkerKinds.Progress:
                    job.Progress?.Report(new AsrProgress(TimeSpan.FromSeconds(e.ProcessedSeconds), TimeSpan.FromSeconds(e.TotalSeconds)));
                    break;
                case WorkerKinds.Segment when e.Segment is not null:
                    return ToSegment(e.Segment, job.Request.Speaker);
                case WorkerKinds.Done:
                    job.Finished = true;
                    if (e.TotalSeconds > 0)
                    {
                        job.Progress?.Report(new AsrProgress(TimeSpan.FromSeconds(e.TotalSeconds), TimeSpan.FromSeconds(e.TotalSeconds)));
                    }

                    return null;
                case WorkerKinds.Error:
                    job.Finished = true;
                    throw new AsrWorkerException("Transcription failed: " + e.Error, null, worker.StderrTail);
            }
        }
    }

    /// <summary>Sends <c>cancel</c> and waits for the job's terminal event; kills the worker when it does not answer in time.</summary>
    private async ValueTask AbortAsync(Job job)
    {
        job.Finished = true;
        if (job.Worker is not { } worker)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(_cancelTimeout);
        try
        {
            await worker.SendAsync(new WorkerRequest { Kind = WorkerKinds.Cancel, RequestId = job.Id }, timeout.Token).ConfigureAwait(false);
            while (await worker.ReadAsync(timeout.Token).ConfigureAwait(false) is { } e)
            {
                if (e.RequestId == job.Id && e.Kind is WorkerKinds.Done or WorkerKinds.Error)
                {
                    return; // The worker stopped the job and stays available.
                }

                if (e.RequestId is null && e.Kind == WorkerKinds.Error)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // No answer in time or the worker is gone.
        }

        await DropWorkerAsync(worker).ConfigureAwait(false);
    }

    private async ValueTask<WorkerConnection> EnsureWorkerAsync(CancellationToken callerToken, CancellationToken token)
    {
        if (_worker is { HasExited: false } running)
        {
            return running;
        }

        if (_worker is { } exited)
        {
            await DropWorkerAsync(exited).ConfigureAwait(false);
        }

        var (fileName, arguments) = BuildCommandLine();
        var workerPath = WorkerPath;
        if (!File.Exists(workerPath))
        {
            throw new AsrWorkerException($"The ASR worker was not found at '{workerPath}'. Set {WorkerPathVariable} or install Murmur.Transcription next to the app.");
        }

        var worker = WorkerConnection.Start(fileName, arguments);
        Interlocked.Increment(ref _workerStarts);
        _worker = worker;
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(_startTimeout);
            WorkerEvent? first;
            try
            {
                first = await worker.ReadAsync(startup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new AsrWorkerException($"The ASR worker did not report ready within {_startTimeout.TotalSeconds:0.#} s" + Describe(worker), worker.ExitCode, worker.StderrTail);
            }

            if (first is null)
            {
                await worker.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                throw new AsrWorkerException("The ASR worker exited during start-up" + Describe(worker), worker.ExitCode, worker.StderrTail);
            }

            if (first.Kind != WorkerKinds.Ready)
            {
                throw new AsrWorkerException($"The ASR worker sent '{first.Kind}' instead of 'ready'" + (first.Error is { } error ? ": " + error : string.Empty), worker.ExitCode, worker.StderrTail);
            }

            return worker;
        }
        catch
        {
            await DropWorkerAsync(worker).ConfigureAwait(false);
            callerToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private async ValueTask StopWorkerAsync()
    {
        if (_worker is { } worker)
        {
            _worker = null;
            await worker.ShutdownAsync(_cancelTimeout).ConfigureAwait(false);
        }
    }

    /// <summary>Kills and forgets <paramref name="worker"/>; the next request starts a fresh one.</summary>
    private async ValueTask DropWorkerAsync(WorkerConnection worker)
    {
        if (ReferenceEquals(_worker, worker))
        {
            _worker = null;
        }

        await worker.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Job(AsrRequest request, IProgress<AsrProgress>? progress, string id)
    {
        public AsrRequest Request { get; } = request;

        public IProgress<AsrProgress>? Progress { get; } = progress;

        public string Id { get; } = id;

        public WorkerConnection? Worker { get; set; }

        public bool Finished { get; set; }

        public bool AnyEvent { get; set; }

        public bool Retried { get; set; }
    }
}
