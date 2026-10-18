using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Murmur.Core.Transcription.Protocol;
using Murmur.Transcription.Pipeline;

namespace Murmur.Transcription.Server;

/// <summary>
/// JSON-lines server (<c>--serve</c>): reads <see cref="WorkerRequest"/> lines, runs transcribe jobs one at a time on
/// a dedicated thread (others wait in a FIFO queue) and writes <see cref="WorkerEvent"/> lines.
/// Every accepted transcribe request ends with exactly one terminal event: <c>done</c> or <c>error</c>.
/// Exit codes: 0 after <c>shutdown</c>, stdin EOF or the idle timeout; 1 after an internal failure.
/// </summary>
internal sealed class WorkerServer
{
    public const string CancelledError = "cancelled";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StopJoinTimeout = TimeSpan.FromSeconds(30);

    private readonly TextReader _input;
    private readonly ProtocolWriter _output;
    private readonly TextWriter _log;
    private readonly Func<ITranscriptionEngine> _engineFactory;
    private readonly TimeSpan _idleExit;

    private readonly object _gate = new();
    private readonly LinkedList<Job> _queue = new();
    private Job? _current;
    private long _idleSince = Stopwatch.GetTimestamp();
    private bool _stopping;
    private Exception? _fatal;
    private ITranscriptionEngine? _engine;

    /// <param name="idleExit">Exit after this long without a running or queued job; <see cref="TimeSpan.Zero"/> or less disables it.</param>
    public WorkerServer(TextReader input, TextWriter output, TextWriter log, Func<ITranscriptionEngine> engineFactory, TimeSpan idleExit)
        : this(input, new ProtocolWriter(output ?? throw new ArgumentNullException(nameof(output))), log, engineFactory, idleExit)
    {
    }

    public WorkerServer(TextReader input, ProtocolWriter output, TextWriter log, Func<ITranscriptionEngine> engineFactory, TimeSpan idleExit)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _idleExit = idleExit;
    }

    public int Run()
    {
        try
        {
            var lines = new BlockingCollection<string?>(); // not disposed: the stdin thread may still be blocked in ReadLine
            var reader = new Thread(() => ReadLines(lines)) { IsBackground = true, Name = "murmur-stdin" };
            var jobThread = new Thread(JobLoop) { IsBackground = true, Name = "murmur-asr-jobs" };

            _output.Write(new WorkerEvent { Kind = WorkerKinds.Ready });
            reader.Start();
            jobThread.Start();

            while (true)
            {
                if (Volatile.Read(ref _fatal) is { } fatal)
                {
                    return Fail(fatal);
                }

                if (!lines.TryTake(out var line, PollInterval))
                {
                    if (IdleExpired())
                    {
                        _log.WriteLine($"murmur-asr: idle for {_idleExit.TotalSeconds:0.#} s, exiting");
                        Stop(jobThread);
                        return 0;
                    }

                    continue;
                }

                if (line is null)
                {
                    _log.WriteLine("murmur-asr: stdin closed, exiting");
                    Stop(jobThread);
                    return 0;
                }

                if (!Handle(line))
                {
                    Stop(jobThread);
                    return 0;
                }
            }
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private int Fail(Exception ex)
    {
        _log.WriteLine("murmur-asr: fatal: " + ex);
        try
        {
            _output.Error(null, "internal error: " + ex.Message);
        }
        catch (Exception writeError)
        {
            _log.WriteLine("murmur-asr: could not report the fatal error: " + writeError.Message);
        }

        lock (_gate)
        {
            _stopping = true;
            _current?.Cancel();
            Monitor.PulseAll(_gate);
        }

        return 1;
    }

    /// <returns><c>false</c> when the server should stop (shutdown request).</returns>
    private bool Handle(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        WorkerRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, WorkerJsonContext.Default.WorkerRequest);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            _output.Error(null, "malformed request: " + ex.Message);
            return true;
        }

        if (request is null)
        {
            _output.Error(null, "malformed request: null");
            return true;
        }

        switch (request.Kind)
        {
            case WorkerKinds.Ping:
                _output.Write(new WorkerEvent { Kind = WorkerKinds.Pong, RequestId = request.RequestId });
                return true;
            case WorkerKinds.Shutdown:
                _log.WriteLine("murmur-asr: shutdown requested");
                return false;
            case WorkerKinds.Transcribe:
                Enqueue(request);
                return true;
            case WorkerKinds.Cancel:
                Cancel(request.RequestId);
                return true;
            default:
                _output.Error(request.RequestId, $"unknown request kind '{request.Kind}'");
                return true;
        }
    }

    private void Enqueue(WorkerRequest request)
    {
        if (string.IsNullOrEmpty(request.RequestId) || string.IsNullOrEmpty(request.AudioPath))
        {
            _output.Error(request.RequestId, "transcribe requires requestId and audioPath");
            return;
        }

        lock (_gate)
        {
            _queue.AddLast(new Job(request.RequestId, request.AudioPath));
            Monitor.PulseAll(_gate);
        }
    }

    private void Cancel(string? requestId)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            _output.Error(null, "cancel requires requestId");
            return;
        }

        lock (_gate)
        {
            if (_current is { } current && current.RequestId == requestId)
            {
                current.Cancel(); // the job thread reports "cancelled" when it stops
                return;
            }

            for (var node = _queue.First; node is not null; node = node.Next)
            {
                if (node.Value.RequestId == requestId)
                {
                    _queue.Remove(node);
                    node.Value.Dispose();
                    _output.Error(requestId, CancelledError);
                    return;
                }
            }
        }

        // Already finished (or never existed): its terminal event was sent, so stay silent on stdout.
        _log.WriteLine($"murmur-asr: cancel for unknown or finished request '{requestId}' ignored");
    }

    private bool IdleExpired()
    {
        if (_idleExit <= TimeSpan.Zero)
        {
            return false;
        }

        lock (_gate)
        {
            return _current is null && _queue.Count == 0 && Stopwatch.GetElapsedTime(_idleSince) >= _idleExit;
        }
    }

    /// <summary>Cancels queued and running jobs (each gets an error "cancelled") and waits for the job thread.</summary>
    private void Stop(Thread jobThread)
    {
        lock (_gate)
        {
            _stopping = true;
            foreach (var job in _queue)
            {
                _output.Error(job.RequestId, CancelledError);
                job.Dispose();
            }

            _queue.Clear();
            _current?.Cancel();
            Monitor.PulseAll(_gate);
        }

        if (jobThread.Join(StopJoinTimeout))
        {
            _engine?.Dispose();
        }
        else
        {
            _log.WriteLine("murmur-asr: job did not stop in time; exiting anyway");
        }
    }

    private void JobLoop()
    {
        try
        {
            while (true)
            {
                Job job;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_stopping)
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_stopping)
                    {
                        return;
                    }

                    job = _queue.First!.Value;
                    _queue.RemoveFirst();
                    _current = job;
                }

                try
                {
                    RunJob(job);
                }
                finally
                {
                    lock (_gate)
                    {
                        _current = null;
                        _idleSince = Stopwatch.GetTimestamp();
                    }

                    job.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _fatal, ex);
        }
    }

    private void RunJob(Job job)
    {
        var id = job.RequestId;
        try
        {
            _engine ??= _engineFactory();
            var sw = Stopwatch.StartNew();
            var stats = _engine.Transcribe(
                job.AudioPath,
                segment => _output.Write(new WorkerEvent { Kind = WorkerKinds.Segment, RequestId = id, Segment = segment }),
                (processed, total) => _output.Write(new WorkerEvent { Kind = WorkerKinds.Progress, RequestId = id, ProcessedSeconds = processed, TotalSeconds = total }),
                job.Token);
            _output.Write(new WorkerEvent { Kind = WorkerKinds.Done, RequestId = id, ProcessedSeconds = stats.AudioSeconds, TotalSeconds = stats.AudioSeconds });
            _log.WriteLine($"murmur-asr: {id}: {stats.AudioSeconds:0.0} s audio, {stats.Segments} segments, {stats.Words} words in {sw.Elapsed.TotalSeconds:0.0} s");
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            _output.Error(id, CancelledError);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.WriteLine($"murmur-asr: {id}: failed: {ex}");
            _output.Error(id, ex.Message);
        }
    }

    private void ReadLines(BlockingCollection<string?> lines)
    {
        try
        {
            string? line;
            while ((line = _input.ReadLine()) is not null)
            {
                lines.Add(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _log.WriteLine("murmur-asr: stdin failed: " + ex.Message);
        }
        finally
        {
            try
            {
                lines.Add(null);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private sealed class Job(string requestId, string audioPath) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public string RequestId { get; } = requestId;

        public string AudioPath { get; } = audioPath;

        public CancellationToken Token => _cts.Token;

        public void Cancel() => _cts.Cancel();

        public void Dispose() => _cts.Dispose();
    }
}
