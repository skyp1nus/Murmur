using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;
using Murmur.Core.Settings;
using Murmur.Intelligence.Llama;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Murmur.Intelligence;

/// <summary>Thrown by <see cref="LocalLlm"/> while a recording is in progress: models are never loaded during recording.</summary>
public sealed class AiPausedException : InvalidOperationException
{
    public AiPausedException()
        : base("AI is paused while recording.")
    {
    }
}

/// <summary>
/// <see cref="ILlm"/> backed by two <c>llama-server</c> sidecars (chat and embeddings). Each server starts on first
/// use, stops after <see cref="LocalLlmOptions.IdleUnload"/> without requests, and is killed as soon as a recording
/// starts. The returned clients stay valid across unloads: each request restarts its server when needed.
/// </summary>
/// <remarks>
/// The model and server paths come from an options provider that is re-read at most every <see cref="OptionsRefreshInterval"/>
/// (and on every request), so models installed, switched or removed while the app runs are noticed without a
/// restart. When the active model of a running server changes, the next request retires that server (an in-flight request
/// finishes on it, then it stops) and starts one with the new model.
/// <para>
/// Acceleration: with a GPU build chosen and installed the chat server runs from it (<c>-ngl 99</c>); the embedding server
/// stays on the CPU build (see <see cref="LocalLlmOptions.AccelerateEmbeddings"/>). A GPU server that fails to start is
/// replaced by the CPU build for the rest of the session (until the choice or the files change) and
/// <see cref="Acceleration"/> says why. The fallback only happens inside a load that is allowed to run: during a recording
/// nothing starts at all.
/// </para>
/// </remarks>
public sealed class LocalLlm : ILlm, IAccelerationStatusSource
{
    /// <summary>How long a resolved set of options is reused before the provider is asked again.</summary>
    public static readonly TimeSpan OptionsRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly Func<LocalLlmOptions> _optionsProvider;
    private readonly Func<bool> _isRecordingActive;
    private readonly ServerStarter _starter;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly ITimer _idleTimer;
    private readonly Lock _lock = new();
    private readonly Slot _chat;
    private readonly Slot _embedding;
    private readonly LocalChatClient _chatClient;
    private readonly LocalEmbeddingGenerator _embeddingGenerator;
    private OptionsSnapshot? _snapshot;

    // Orders the status events: the status is computed from the latest snapshot and compared with the last one published under it.
    private readonly Lock _statusLock = new();

    // The GPU build that failed to start in this session (written and read without the lock; replaced as a whole).
    private AccelerationFailure? _accelerationFailure;
    private AccelerationStatus? _publishedStatus;

    // Servers being stopped gracefully (idle, model switch): recording start kills them, unload and dispose wait for them.
    private readonly Dictionary<ILlamaServer, StopEntry> _stopping = [];
    private bool _disposed;

    // The shared idle timer is armed for the earliest due slot; guarded by _lock.
    private bool _idleArmed;
    private long _idleArmedAt;
    private TimeSpan _idleArmedDelay;

    /// <param name="options">Model and server paths.</param>
    /// <param name="isRecordingActive">Returns <c>true</c> while a recording is in progress.</param>
    public LocalLlm(LocalLlmOptions options, Func<bool> isRecordingActive)
        : this(Fixed(options), isRecordingActive)
    {
    }

    /// <param name="optionsProvider">Resolves the current model and server paths; called at most every <see cref="OptionsRefreshInterval"/> for status queries and on every request.</param>
    /// <param name="isRecordingActive">Returns <c>true</c> while a recording is in progress.</param>
    public LocalLlm(Func<LocalLlmOptions> optionsProvider, Func<bool> isRecordingActive)
        : this(optionsProvider, isRecordingActive, StartProcessAsync, TimeProvider.System, httpClient: null)
    {
    }

    internal LocalLlm(LocalLlmOptions options, Func<bool> isRecordingActive, ServerStarter starter, TimeProvider time, HttpClient? httpClient)
        : this(Fixed(options), isRecordingActive, starter, time, httpClient)
    {
    }

    internal LocalLlm(Func<LocalLlmOptions> optionsProvider, Func<bool> isRecordingActive, ServerStarter starter, TimeProvider time, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(optionsProvider);
        ArgumentNullException.ThrowIfNull(isRecordingActive);
        ArgumentNullException.ThrowIfNull(starter);
        ArgumentNullException.ThrowIfNull(time);
        _optionsProvider = optionsProvider;
        _isRecordingActive = isRecordingActive;
        _starter = starter;
        _time = time;
        _http = httpClient ?? OpenAiHttp.CreateDefaultClient();
        _chat = new Slot(embedding: false);
        _embedding = new Slot(embedding: true);
        _chatClient = new LocalChatClient(this);
        _embeddingGenerator = new LocalEmbeddingGenerator(this);
        _idleTimer = time.CreateTimer(static state => ((LocalLlm)state!).OnIdleTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal delegate Task<ILlamaServer> ServerStarter(LlamaServerOptions options, CancellationToken cancellationToken);

    public LlmStatus Status
    {
        get
        {
            if (!IsInstalled)
            {
                return LlmStatus.NotInstalled;
            }

            lock (_lock)
            {
                if (_chat.Loading || _embedding.Loading)
                {
                    return LlmStatus.Loading;
                }

                return _chat.Server is not null || _embedding.Server is not null ? LlmStatus.Ready : LlmStatus.Unloaded;
            }
        }
    }

    /// <summary>The server binary and at least one model file exist.</summary>
    public bool IsInstalled
    {
        get
        {
            var options = GetOptions();
            return FileExists(options.LlamaServerPath) && (FileExists(options.ChatModelPath) || FileExists(options.EmbeddingModelPath));
        }
    }

    /// <summary>
    /// Re-resolves the options right now, so <see cref="IsInstalled"/> and <see cref="Status"/> see a model that was
    /// just installed, switched or removed without waiting for <see cref="OptionsRefreshInterval"/>.
    /// </summary>
    public void Refresh()
    {
        GetSnapshot(force: true);
        PublishStatus();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Handlers run synchronously under a small private lock (so events cannot be reordered) on whatever thread changed the
    /// state: keep them short and marshal to the UI thread. The event is raised from the code paths that change state
    /// (<see cref="Refresh"/>, <see cref="RetireStaleServers"/>, a model load, a GPU server that failed to start), never
    /// from the <see cref="Current"/> getter.
    /// </remarks>
    public event Action<AccelerationStatus>? Changed;

    /// <summary>
    /// The build the chat server uses and why it is not the chosen one: <see cref="AccelerationFallback.RuntimeNotInstalled"/>
    /// = download it first, <see cref="AccelerationFallback.StartFailed"/> = "GPU unavailable: <see cref="AccelerationStatus.Detail"/>".
    /// Computed on every read from the options as last resolved (at most <see cref="OptionsRefreshInterval"/> old); reading raises nothing.
    /// </summary>
    public AccelerationStatus Current => ComputeStatus(GetSnapshot());

    private AccelerationStatus ComputeStatus(OptionsSnapshot snapshot)
    {
        var options = snapshot.Options;
        if (options.Acceleration == AiAcceleration.Cpu)
        {
            return new AccelerationStatus(AiAcceleration.Cpu, AiAcceleration.Cpu, AccelerationFallback.None);
        }

        if (!snapshot.AcceleratedInstalled)
        {
            return new AccelerationStatus(options.Acceleration, AiAcceleration.Cpu, AccelerationFallback.RuntimeNotInstalled);
        }

        return FailureFor(options, options.ChatModelPath) is { } failure
            ? new AccelerationStatus(options.Acceleration, AiAcceleration.Cpu, AccelerationFallback.StartFailed, failure.Reason)
            : new AccelerationStatus(options.Acceleration, options.Acceleration, AccelerationFallback.None);
    }

    /// <summary>
    /// Computes the status from the latest snapshot and raises <see cref="Changed"/> when it differs from the one published
    /// last (the first one is only remembered). Compute, compare and raise happen under one lock, so two threads cannot publish out of order.
    /// </summary>
    private void PublishStatus()
    {
        lock (_statusLock)
        {
            if (Volatile.Read(ref _snapshot) is not { } snapshot)
            {
                return;
            }

            var status = ComputeStatus(snapshot);
            var previous = _publishedStatus;
            _publishedStatus = status;
            if (previous is null || previous == status)
            {
                return;
            }

            try
            {
                Changed?.Invoke(status);
            }
            catch (Exception)
            {
                // A faulty subscriber must not break model loading.
            }
        }
    }

    /// <summary>
    /// Stops the idle servers that no longer match the configuration (another acceleration or model was chosen), so a
    /// GPU build does not keep its VRAM after the user went back to the CPU. Servers with a request in flight are left
    /// alone; every server restarts lazily, with the new configuration, at its next request. No-op during a recording
    /// (the servers are gone already).
    /// </summary>
    public void RetireStaleServers()
    {
        var snapshot = GetSnapshot(force: true);
        PublishStatus();
        var toStop = new List<(ILlamaServer Server, TaskCompletionSource? Stopping)>(2);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var slot in (ReadOnlySpan<Slot>)[_chat, _embedding])
            {
                if (slot.Server is { } server && slot.InFlight == 0 && !Matches(slot, snapshot))
                {
                    toStop.Add((server, MarkStopping(server, slot.RunningModelPath)));
                    slot.Server = null;
                }
            }
        }

        foreach (var (server, stopping) in toStop)
        {
            StartStop(server, stopping);
        }
    }

    public async ValueTask<IChatClient> GetChatClientAsync(CancellationToken cancellationToken)
    {
        await WarmUpAsync(_chat, cancellationToken).ConfigureAwait(false);
        return _chatClient;
    }

    public async ValueTask<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(CancellationToken cancellationToken)
    {
        await WarmUpAsync(_embedding, cancellationToken).ConfigureAwait(false);
        return _embeddingGenerator;
    }

    /// <summary>
    /// Kills both servers right away (no graceful wait) so no model memory stays allocated while recording.
    /// Requests in flight fail; new ones throw <see cref="AiPausedException"/> while the recording lasts.
    /// </summary>
    public void NotifyRecordingStarted()
    {
        foreach (var held in Detach(_chat, _embedding))
        {
            held.Server.Kill();
            _ = DisposeQuietlyAsync(held.Server);
        }

        // Servers already on their way down must not linger into the recording either.
        foreach (var server in SnapshotStopping().Keys)
        {
            server.Kill();
        }
    }

    public async ValueTask UnloadAsync(CancellationToken cancellationToken)
    {
        // Detached servers are registered as stopping inside Detach's lock, so a recording that starts right now kills them.
        var detached = Detach(stop: true, [_chat, _embedding]);
        foreach (var held in detached)
        {
            StartStop(held.Server, held.Stopping);
        }

        // Return only when no process still holds the model or the executable.
        await Task.WhenAll(SnapshotStopping().Values).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await _idleTimer.DisposeAsync().ConfigureAwait(false);
        foreach (var held in Detach(_chat, _embedding))
        {
            held.Server.Kill();
            await DisposeQuietlyAsync(held.Server).ConfigureAwait(false);
        }

        var stopping = SnapshotStopping();
        foreach (var server in stopping.Keys)
        {
            server.Kill();
        }

        await Task.WhenAll(stopping.Values).ConfigureAwait(false);
        _http.Dispose();
    }

    private static async Task<ILlamaServer> StartProcessAsync(LlamaServerOptions options, CancellationToken cancellationToken) =>
        await LlamaServerProcess.StartAsync(options, cancellationToken).ConfigureAwait(false);

    private static bool FileExists(string? path) => !string.IsNullOrEmpty(path) && File.Exists(path);

    private static string? ModelId(string? path) => path is null ? null : Path.GetFileNameWithoutExtension(path);

    private static Func<LocalLlmOptions> Fixed(LocalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return () => options;
    }

    private static string? ModelPathOf(LocalLlmOptions options, Slot slot) =>
        slot.IsEmbedding ? options.EmbeddingModelPath : options.ChatModelPath;

    /// <summary>The options as last resolved, never refreshed (safe to call under <see cref="_lock"/>).</summary>
    private LocalLlmOptions LastOptions => Volatile.Read(ref _snapshot)?.Options ?? GetOptions();

    private LocalLlmOptions GetOptions(bool force = false) => GetSnapshot(force).Options;

    /// <summary>
    /// The resolved options plus the one file check they need (is the GPU build installed), reused for
    /// <see cref="OptionsRefreshInterval"/>, re-read from the provider after that or when forced. Resolving here, once per
    /// snapshot, keeps file system calls out of the sections that run under <see cref="_lock"/>.
    /// </summary>
    private OptionsSnapshot GetSnapshot(bool force = false)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var now = _time.GetTimestamp();
        if (!force && snapshot is not null && _time.GetElapsedTime(snapshot.At, now) < OptionsRefreshInterval)
        {
            return snapshot;
        }

        var options = _optionsProvider() ?? throw new InvalidOperationException("The options provider returned null.");
        var fresh = new OptionsSnapshot(options, now, options.Acceleration != AiAcceleration.Cpu && FileExists(options.AcceleratedServerPath));
        Volatile.Write(ref _snapshot, fresh);

        // Choosing another build (or going back to the CPU) forgets a failed GPU start: choosing the GPU again is the retry.
        if (Volatile.Read(ref _accelerationFailure) is { } failure && failure.Requested != options.Acceleration)
        {
            Interlocked.CompareExchange(ref _accelerationFailure, null, failure);
        }

        return fresh;
    }

    private bool Matches(Slot slot, OptionsSnapshot snapshot) =>
        string.Equals(slot.RunningModelPath, ModelPathOf(snapshot.Options, slot), StringComparison.Ordinal)
        && string.Equals(slot.RunningServerPath, ServerPathFor(slot, snapshot, out _), StringComparison.Ordinal);

    /// <summary>
    /// The recorded failure of the GPU build <paramref name="options"/> asks for with <paramref name="modelPath"/>, or <c>null</c>.
    /// A failure belongs to the model it failed with: another model may fit the VRAM, so choosing one is a new attempt.
    /// </summary>
    private AccelerationFailure? FailureFor(LocalLlmOptions options, string? modelPath) =>
        Volatile.Read(ref _accelerationFailure) is { } failure
        && failure.Requested == options.Acceleration
        && string.Equals(failure.ServerPath, options.AcceleratedServerPath, StringComparison.Ordinal)
        && string.Equals(failure.ModelPath, modelPath, StringComparison.Ordinal)
            ? failure
            : null;

    /// <summary>
    /// The binary the slot's server runs from: the chosen GPU build when it is installed and has not failed in this
    /// session (the chat server; the embedding one only with <see cref="LocalLlmOptions.AccelerateEmbeddings"/>), else the CPU build.
    /// </summary>
    private string? ServerPathFor(Slot slot, OptionsSnapshot snapshot, out bool accelerated)
    {
        var options = snapshot.Options;
        accelerated = snapshot.AcceleratedInstalled
            && (!slot.IsEmbedding || options.AccelerateEmbeddings)
            && FailureFor(options, ModelPathOf(options, slot)) is null;
        return accelerated ? options.AcceleratedServerPath : options.LlamaServerPath;
    }

    private static IReadOnlyList<string> ArgumentsFor(Slot slot, LocalLlmOptions options, bool accelerated)
    {
        var extra = slot.IsEmbedding ? options.EmbeddingExtraArguments : options.ChatExtraArguments;
        if (!accelerated)
        {
            return extra;
        }

        // --device pins the GPU: without it a build whose GPU backend finds no device would start a healthy CPU-only server
        // ("no usable GPU found, --gpu-layers ignored") instead of failing. The GPU arguments come before the extra ones,
        // so an explicit -ngl in the extra arguments still wins.
        return
        [
            "--device", LocalLlmOptions.DeviceName(options.Acceleration),
            .. slot.IsEmbedding ? options.AcceleratedEmbeddingArguments : options.AcceleratedChatArguments,
            .. extra,
        ];
    }

    private LlamaServerOptions CreateServerOptions(Slot slot, OptionsSnapshot snapshot, bool allowAcceleration, out bool accelerated)
    {
        var options = snapshot.Options;
        string? serverPath;
        if (allowAcceleration)
        {
            serverPath = ServerPathFor(slot, snapshot, out accelerated);
        }
        else
        {
            serverPath = options.LlamaServerPath;
            accelerated = false;
        }

        if (!FileExists(serverPath))
        {
            throw new FileNotFoundException("llama-server is not installed.", serverPath);
        }

        var modelPath = ModelPathOf(options, slot);
        if (!FileExists(modelPath))
        {
            throw new FileNotFoundException(
                slot.IsEmbedding ? "The embedding model is not installed." : "The chat model is not installed.", modelPath);
        }

        return new LlamaServerOptions
        {
            ServerPath = serverPath!,
            ModelPath = modelPath!,
            Threads = options.Threads,
            ContextSize = slot.IsEmbedding ? options.EmbeddingContextSize : options.ContextSize,
            Embedding = slot.IsEmbedding,
            ExtraArguments = ArgumentsFor(slot, options, accelerated),
            RequireGpu = accelerated,
            StartupTimeout = options.StartupTimeout,
        };
    }

    /// <summary>Remembers the failed GPU start of <paramref name="modelPath"/> (the CPU build serves from now on) and tells the listeners.</summary>
    /// <returns>The record and what it replaced, for <see cref="UndoAccelerationFailure"/>.</returns>
    private (AccelerationFailure Recorded, AccelerationFailure? Previous) RecordAccelerationFailure(LocalLlmOptions options, string? modelPath, Exception error)
    {
        var recorded = new AccelerationFailure(options.Acceleration, options.AcceleratedServerPath, modelPath, DescribeFailure(error));
        var previous = Interlocked.Exchange(ref _accelerationFailure, recorded);
        PublishStatus();
        return (recorded, previous);
    }

    /// <summary>
    /// The CPU build failed to start too, so the GPU was not the culprit (a broken model file, no memory at all): take the
    /// failure back, unless something newer replaced it, and say so.
    /// </summary>
    private void UndoAccelerationFailure((AccelerationFailure Recorded, AccelerationFailure? Previous) record)
    {
        Interlocked.CompareExchange(ref _accelerationFailure, record.Previous, record.Recorded);
        PublishStatus();
    }

    /// <summary>
    /// One short line for the UI: the last line of the server output that looks like an error (<c>error</c>, <c>failed</c>,
    /// <c>out of memory</c>, <c>ErrorOutOfDeviceMemory</c>, <c>no usable GPU</c>), else its last line, else the first line of the exception.
    /// </summary>
    internal static string DescribeFailure(Exception error)
    {
        var line = error is LlamaServerException { OutputTail.Length: > 0 } server ? PickLine(server.OutputTail) : null;
        line ??= FirstLine(error is TimeoutException ? "llama-server did not become healthy in time." : error.Message);
        line = line.Trim();
        if (line.Length > 200)
        {
            line = line[..200] + "...";
        }

        return line.Length == 0 ? error.GetType().Name : line;
    }

    private static readonly string[] ErrorMarkers = ["error", "failed", "out of memory", "ErrorOutOfDeviceMemory", "no usable GPU"];

    /// <summary>Scans the lines from the end without splitting the text.</summary>
    private static string? PickLine(ReadOnlySpan<char> text)
    {
        ReadOnlySpan<char> lastNonEmpty = default;
        var end = text.Length;
        while (end > 0)
        {
            var start = text[..end].LastIndexOf('\n') + 1;
            var line = text[start..end].Trim();
            if (line.Length > 0)
            {
                if (lastNonEmpty.IsEmpty)
                {
                    lastNonEmpty = line;
                }

                foreach (var marker in ErrorMarkers)
                {
                    if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        return line.ToString();
                    }
                }
            }

            end = Math.Max(0, start - 1);
        }

        return lastNonEmpty.IsEmpty ? null : lastNonEmpty.ToString();
    }

    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline];
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isRecordingActive())
        {
            throw new AiPausedException();
        }
    }

    private async ValueTask WarmUpAsync(Slot slot, CancellationToken cancellationToken)
    {
        var server = await AcquireAsync(slot, cancellationToken).ConfigureAwait(false);
        Release(slot, server);
    }

    /// <summary>Starts the slot's server if needed and counts a request in flight; pair with <see cref="Release"/>.</summary>
    private async ValueTask<ILlamaServer> AcquireAsync(Slot slot, CancellationToken cancellationToken)
    {
        while (true)
        {
            var server = await EnsureStartedAsync(slot, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (ReferenceEquals(slot.Server, server))
                {
                    slot.InFlight++;
                    slot.LastUse = _time.GetTimestamp();
                    return server;
                }
            }

            // Unloaded between start and use; start again (or fail the guard).
        }
    }

    private void Release(Slot slot, ILlamaServer server)
    {
        ILlamaServer? retiredDone = null;
        TaskCompletionSource? stopping = null;
        lock (_lock)
        {
            if (ReferenceEquals(slot.Server, server))
            {
                slot.InFlight--;
                slot.LastUse = _time.GetTimestamp();
                if (!_disposed && slot.InFlight == 0)
                {
                    // Never postpone a pending (earlier) fire: it may be due for the other slot.
                    // OnIdleTimer recomputes every slot's due time and re-arms for the next one.
                    ArmIdleTimerIfEarlier(LastOptions.IdleUnload, slot.LastUse);
                }
            }
            else if (slot.Retired.TryGetValue(server, out var retired))
            {
                // A retired server (its model was switched) stops once its last request is done.
                if (retired.Remaining <= 1)
                {
                    slot.Retired.Remove(server);
                    retiredDone = server;
                    stopping = MarkStopping(server, retired.ModelPath);
                }
                else
                {
                    retired.Remaining--;
                }
            }
        }

        if (retiredDone is not null)
        {
            StartStop(retiredDone, stopping);
        }
    }

    /// <summary>Arms the idle timer for <paramref name="delay"/> from <paramref name="now"/> unless it is already due sooner. Call under <see cref="_lock"/>.</summary>
    private void ArmIdleTimerIfEarlier(TimeSpan delay, long now)
    {
        if (delay == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        if (_idleArmed && _idleArmedDelay - _time.GetElapsedTime(_idleArmedAt, now) <= delay)
        {
            return;
        }

        _idleArmed = true;
        _idleArmedAt = now;
        _idleArmedDelay = delay;
        _idleTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private async ValueTask<ILlamaServer> EnsureStartedAsync(Slot slot, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();

        // A request is rare and slow next to resolving a few paths: always look at the current configuration.
        var snapshot = GetSnapshot(force: true);
        PublishStatus();
        lock (_lock)
        {
            if (slot.Server is { HasExited: false } running && Matches(slot, snapshot))
            {
                return running;
            }
        }

        await slot.StartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();

            snapshot = GetSnapshot(force: true);
            var options = snapshot.Options;
            ILlamaServer? dead = null;
            ILlamaServer? retiredIdle = null;
            TaskCompletionSource? retiredStopping = null;
            CancellationToken lifetime;
            lock (_lock)
            {
                if (slot.Server is { HasExited: false } running && Matches(slot, snapshot))
                {
                    return running;
                }

                // Taken together with the removal of the old server: a recording that starts while the old one stops
                // cancels exactly this token (see Detach), so the new model never loads under it.
                lifetime = slot.Lifetime.Token;

                if (slot.Server is { } old)
                {
                    slot.Server = null;
                    if (old.HasExited)
                    {
                        dead = old;
                    }
                    else if (slot.InFlight > 0)
                    {
                        // Its model was switched or removed: requests in flight finish on it, the last one stops it.
                        slot.Retired[old] = new RetiredEntry(slot.InFlight, slot.RunningModelPath);
                    }
                    else
                    {
                        retiredIdle = old;
                        retiredStopping = MarkStopping(old, slot.RunningModelPath);
                    }

                    slot.InFlight = 0;
                }
            }

            if (retiredIdle is not null)
            {
                StartStop(retiredIdle, retiredStopping);
                await WaitStoppedAsync(retiredIdle).ConfigureAwait(false);
            }

            if (dead is not null)
            {
                await DisposeQuietlyAsync(dead).ConfigureAwait(false);
            }

            ThrowIfUnavailable();
            if (lifetime.IsCancellationRequested)
            {
                throw new OperationCanceledException("The model was unloaded while the previous one was stopping.", lifetime);
            }

            var serverOptions = CreateServerOptions(slot, snapshot, allowAcceleration: true, out var accelerated);
            lock (_lock)
            {
                slot.Loading = true;
            }

            ILlamaServer server;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
                try
                {
                    server = await _starter(serverOptions, linked.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (accelerated && ex is not OperationCanceledException)
                {
                    // The GPU build could not start (driver missing, out of VRAM, ...): the CPU build serves instead. Not while a
                    // recording runs (no model may load then) and not when the load was cancelled.
                    var record = RecordAccelerationFailure(options, ModelPathOf(options, slot), ex);
                    ThrowIfUnavailable();
                    linked.Token.ThrowIfCancellationRequested();
                    serverOptions = CreateServerOptions(slot, snapshot, allowAcceleration: false, out _);
                    try
                    {
                        server = await _starter(serverOptions, linked.Token).ConfigureAwait(false);
                    }
                    catch (Exception cpuError) when (cpuError is not OperationCanceledException)
                    {
                        // Not the GPU's fault after all: do not blame it for the rest of the session.
                        UndoAccelerationFailure(record);
                        throw;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lifetime.IsCancellationRequested)
            {
                // Unloaded (recording started or UnloadAsync) while loading.
                ThrowIfUnavailable();
                throw;
            }
            finally
            {
                lock (_lock)
                {
                    slot.Loading = false;
                }
            }

            bool accepted;
            lock (_lock)
            {
                accepted = !_disposed && slot.Lifetime.Token == lifetime && !lifetime.IsCancellationRequested;
                if (accepted)
                {
                    slot.Server = server;
                    slot.RunningModelPath = serverOptions.ModelPath;
                    slot.RunningServerPath = serverOptions.ServerPath;
                    slot.InFlight = 0;
                    slot.LastUse = _time.GetTimestamp();
                }
            }

            if (!accepted || _isRecordingActive())
            {
                if (accepted)
                {
                    // Detach returns the new server and any retired one still serving a request: all of them go.
                    foreach (var detached in Detach(slot))
                    {
                        detached.Server.Kill();
                        await DisposeQuietlyAsync(detached.Server).ConfigureAwait(false);
                    }
                }
                else
                {
                    server.Kill();
                    await DisposeQuietlyAsync(server).ConfigureAwait(false);
                }

                ThrowIfUnavailable();
                throw new OperationCanceledException("The model was unloaded while it was loading.");
            }

            return server;
        }
        finally
        {
            slot.StartGate.Release();
        }
    }

    /// <summary>Takes the servers out of their slots and cancels loads in progress.</summary>
    private List<Held> Detach(params ReadOnlySpan<Slot> slots) => Detach(stop: false, slots);

    /// <param name="stop">Register the servers as stopping in the same lock section (the caller starts the stop with <see cref="StartStop"/>).</param>
    private List<Held> Detach(bool stop, ReadOnlySpan<Slot> slots)
    {
        var servers = new List<Held>(2);
        var lifetimes = new List<CancellationTokenSource>(slots.Length);
        lock (_lock)
        {
            foreach (var slot in slots)
            {
                lifetimes.Add(slot.Lifetime);
                slot.Lifetime = new CancellationTokenSource();
                if (slot.Server is { } server)
                {
                    servers.Add(new Held(server, stop ? MarkStopping(server, slot.RunningModelPath) : null));
                    slot.Server = null;
                }

                // Retired servers still serving a request go down with the rest.
                foreach (var (retired, entry) in slot.Retired)
                {
                    servers.Add(new Held(retired, stop ? MarkStopping(retired, entry.ModelPath) : null));
                }

                slot.Retired.Clear();
                slot.InFlight = 0;
            }
        }

        // Outside the lock: cancellation callbacks may resume a loader that takes the lock.
        foreach (var lifetime in lifetimes)
        {
            lifetime.Cancel();
        }

        return servers;
    }

    private void OnIdleTimer()
    {
        var toStop = new List<(ILlamaServer Server, TaskCompletionSource? Stopping)>(2);
        TimeSpan? nextDue = null;
        var idleUnload = LastOptions.IdleUnload;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _idleArmed = false;
            foreach (var slot in (ReadOnlySpan<Slot>)[_chat, _embedding])
            {
                if (slot.Server is not { } server)
                {
                    continue;
                }

                var idle = _time.GetElapsedTime(slot.LastUse);
                if (slot.InFlight == 0 && idle >= idleUnload)
                {
                    toStop.Add((server, MarkStopping(server, slot.RunningModelPath)));
                    slot.Server = null;
                    continue;
                }

                var due = slot.InFlight > 0 ? idleUnload : idleUnload - idle;
                nextDue = nextDue is { } current && current < due ? current : due;
            }

            if (nextDue is { } next)
            {
                ArmIdleTimerIfEarlier(next, _time.GetTimestamp());
            }
        }

        foreach (var (server, stopping) in toStop)
        {
            StartStop(server, stopping);
        }
    }

    private Dictionary<ILlamaServer, Task> SnapshotStopping()
    {
        lock (_lock)
        {
            var snapshot = new Dictionary<ILlamaServer, Task>(_stopping.Count);
            foreach (var (server, entry) in _stopping)
            {
                snapshot[server] = entry.Done.Task;
            }

            return snapshot;
        }
    }

    /// <summary>
    /// Registers a server that was just taken out of its slot as stopping. Call under <see cref="_lock"/>, in the same
    /// section that removed it, so a recording start can never miss it; then run <see cref="StartStop"/> outside the lock.
    /// </summary>
    /// <returns>The completion source for <see cref="StartStop"/>, or <c>null</c> when the server is already stopping.</returns>
    private TaskCompletionSource? MarkStopping(ILlamaServer server, string? modelPath)
    {
        if (_stopping.ContainsKey(server))
        {
            return null;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopping[server] = new StopEntry(modelPath, done);
        return done;
    }

    /// <summary>Stops a server registered by <see cref="MarkStopping"/> gracefully in the background.</summary>
    private void StartStop(ILlamaServer server, TaskCompletionSource? stopping)
    {
        if (stopping is not null)
        {
            _ = RunStopAsync(server, stopping);
        }
    }

    private async Task WaitStoppedAsync(ILlamaServer server)
    {
        Task? task;
        lock (_lock)
        {
            task = _stopping.TryGetValue(server, out var entry) ? entry.Done.Task : null;
        }

        if (task is not null)
        {
            await task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a live, retired or stopping server still has <paramref name="modelPath"/> open, so the file cannot be
    /// deleted yet. Call <see cref="UnloadAsync"/> first when it does.
    /// </summary>
    public bool IsUsingModel(string modelPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var wanted = Path.GetFullPath(modelPath);
        lock (_lock)
        {
            foreach (var slot in (ReadOnlySpan<Slot>)[_chat, _embedding])
            {
                if (slot.Server is not null && Same(slot.RunningModelPath))
                {
                    return true;
                }

                foreach (var retired in slot.Retired.Values)
                {
                    if (Same(retired.ModelPath))
                    {
                        return true;
                    }
                }
            }

            foreach (var entry in _stopping.Values)
            {
                if (Same(entry.ModelPath))
                {
                    return true;
                }
            }
        }

        return false;

        bool Same(string? path) => path is not null && string.Equals(Path.GetFullPath(path), wanted, comparison);
    }

    private async Task RunStopAsync(ILlamaServer server, TaskCompletionSource done)
    {
        try
        {
            await StopQuietlyAsync(server, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _stopping.Remove(server);
            }

            done.TrySetResult();
        }
    }

    private static async Task StopQuietlyAsync(ILlamaServer server, CancellationToken cancellationToken)
    {
        try
        {
            await server.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            server.Kill();
        }
        finally
        {
            await DisposeQuietlyAsync(server).ConfigureAwait(false);
        }
    }

    private static async Task DisposeQuietlyAsync(ILlamaServer server)
    {
        try
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Process already gone.
        }
    }

    private sealed record OptionsSnapshot(LocalLlmOptions Options, long At, bool AcceleratedInstalled);

    private sealed record AccelerationFailure(AiAcceleration Requested, string? ServerPath, string? ModelPath, string Reason);

    private readonly record struct Held(ILlamaServer Server, TaskCompletionSource? Stopping);

    private sealed record StopEntry(string? ModelPath, TaskCompletionSource Done);

    private sealed class RetiredEntry(int remaining, string? modelPath)
    {
        public int Remaining { get; set; } = remaining;

        public string? ModelPath { get; } = modelPath;
    }

    private sealed class Slot(bool embedding)
    {
        /// <summary>Model file the running <see cref="Server"/> was started with.</summary>
        public string? RunningModelPath { get; set; }

        /// <summary><c>llama-server</c> binary the running <see cref="Server"/> was started from.</summary>
        public string? RunningServerPath { get; set; }

        /// <summary>Servers replaced after a model switch that still have requests in flight (requests left and the model it holds).</summary>
        public Dictionary<ILlamaServer, RetiredEntry> Retired { get; } = [];

        public bool IsEmbedding { get; } = embedding;

        public SemaphoreSlim StartGate { get; } = new(1, 1);

        public ILlamaServer? Server { get; set; }

        public bool Loading { get; set; }

        public int InFlight { get; set; }

        public long LastUse { get; set; }

        public CancellationTokenSource Lifetime { get; set; } = new();
    }

    /// <summary>Chat facade: every request goes through <see cref="AcquireAsync"/>, so it survives unloads.</summary>
    private sealed class LocalChatClient(LocalLlm owner) : IChatClient
    {
        private ChatClientMetadata Metadata => new("llama.cpp", null, ModelId(owner.GetOptions().ChatModelPath));

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var server = await owner.AcquireAsync(owner._chat, cancellationToken).ConfigureAwait(false);
            try
            {
                using var client = new LlamaChatClient(server.BaseAddress, owner._http, Metadata.DefaultModelId);
                return await client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                owner.Release(owner._chat, server);
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var server = await owner.AcquireAsync(owner._chat, cancellationToken).ConfigureAwait(false);
            try
            {
                using var client = new LlamaChatClient(server.BaseAddress, owner._http, Metadata.DefaultModelId);
                await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                {
                    yield return update;
                }
            }
            finally
            {
                owner.Release(owner._chat, server);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            if (serviceKey is not null)
            {
                return null;
            }

            if (serviceType == typeof(ChatClientMetadata))
            {
                return Metadata;
            }

            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Owned by LocalLlm.
        }
    }

    /// <summary>Embedding facade: every request goes through <see cref="AcquireAsync"/>, so it survives unloads.</summary>
    private sealed class LocalEmbeddingGenerator(LocalLlm owner) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private EmbeddingGeneratorMetadata Metadata => new("llama.cpp", null, ModelId(owner.GetOptions().EmbeddingModelPath));

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var server = await owner.AcquireAsync(owner._embedding, cancellationToken).ConfigureAwait(false);
            try
            {
                using var generator = new LlamaEmbeddingGenerator(
                    server.BaseAddress, owner._http, Metadata.DefaultModelId, owner.GetOptions().EmbeddingBatchSize);
                return await generator.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                owner.Release(owner._embedding, server);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            if (serviceKey is not null)
            {
                return null;
            }

            if (serviceType == typeof(EmbeddingGeneratorMetadata))
            {
                return Metadata;
            }

            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Owned by LocalLlm.
        }
    }
}
