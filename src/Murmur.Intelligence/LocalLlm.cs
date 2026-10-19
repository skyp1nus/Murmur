using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;
using Murmur.Intelligence.Llama;

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
/// </remarks>
public sealed class LocalLlm : ILlm
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

    // Servers being stopped gracefully (idle, model switch): recording start kills them, unload and dispose wait for them.
    private readonly Dictionary<ILlamaServer, Task> _stopping = [];
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
    public void Refresh() => GetOptions(force: true);

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
        foreach (var server in Detach(_chat, _embedding))
        {
            server.Kill();
            _ = DisposeQuietlyAsync(server);
        }

        // Servers already on their way down must not linger into the recording either.
        foreach (var server in SnapshotStopping().Keys)
        {
            server.Kill();
        }
    }

    public async ValueTask UnloadAsync(CancellationToken cancellationToken)
    {
        foreach (var server in Detach(_chat, _embedding))
        {
            await StopQuietlyAsync(server, cancellationToken).ConfigureAwait(false);
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
        foreach (var server in Detach(_chat, _embedding))
        {
            server.Kill();
            await DisposeQuietlyAsync(server).ConfigureAwait(false);
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

    /// <summary>The resolved options: reused for <see cref="OptionsRefreshInterval"/>, re-read from the provider after that or when forced.</summary>
    private LocalLlmOptions GetOptions(bool force = false)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var now = _time.GetTimestamp();
        if (!force && snapshot is not null && _time.GetElapsedTime(snapshot.At, now) < OptionsRefreshInterval)
        {
            return snapshot.Options;
        }

        var options = _optionsProvider() ?? throw new InvalidOperationException("The options provider returned null.");
        Volatile.Write(ref _snapshot, new OptionsSnapshot(options, now));
        return options;
    }

    private static bool Matches(Slot slot, LocalLlmOptions options) =>
        string.Equals(slot.RunningModelPath, ModelPathOf(options, slot), StringComparison.Ordinal)
        && string.Equals(slot.RunningServerPath, options.LlamaServerPath, StringComparison.Ordinal);

    private LlamaServerOptions CreateServerOptions(Slot slot, LocalLlmOptions options)
    {
        var serverPath = options.LlamaServerPath;
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
            ExtraArguments = slot.IsEmbedding ? options.EmbeddingExtraArguments : options.ChatExtraArguments,
            StartupTimeout = options.StartupTimeout,
        };
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
            else if (slot.Retired.TryGetValue(server, out var remaining))
            {
                // A retired server (its model was switched) stops once its last request is done.
                if (remaining <= 1)
                {
                    slot.Retired.Remove(server);
                    retiredDone = server;
                }
                else
                {
                    slot.Retired[server] = remaining - 1;
                }
            }
        }

        if (retiredDone is not null)
        {
            _ = BeginStop(retiredDone);
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
        var options = GetOptions(force: true);
        lock (_lock)
        {
            if (slot.Server is { HasExited: false } running && Matches(slot, options))
            {
                return running;
            }
        }

        await slot.StartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();

            options = GetOptions(force: true);
            ILlamaServer? dead = null;
            ILlamaServer? retiredIdle = null;
            CancellationToken lifetime;
            lock (_lock)
            {
                if (slot.Server is { HasExited: false } running && Matches(slot, options))
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
                        slot.Retired[old] = slot.InFlight;
                    }
                    else
                    {
                        retiredIdle = old;
                    }

                    slot.InFlight = 0;
                }
            }

            if (retiredIdle is not null)
            {
                await BeginStop(retiredIdle).ConfigureAwait(false);
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

            var serverOptions = CreateServerOptions(slot, options);
            lock (_lock)
            {
                slot.Loading = true;
            }

            ILlamaServer server;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
                server = await _starter(serverOptions, linked.Token).ConfigureAwait(false);
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
                        detached.Kill();
                        await DisposeQuietlyAsync(detached).ConfigureAwait(false);
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
    private List<ILlamaServer> Detach(params ReadOnlySpan<Slot> slots)
    {
        var servers = new List<ILlamaServer>(slots.Length);
        var lifetimes = new List<CancellationTokenSource>(slots.Length);
        lock (_lock)
        {
            foreach (var slot in slots)
            {
                lifetimes.Add(slot.Lifetime);
                slot.Lifetime = new CancellationTokenSource();
                if (slot.Server is { } server)
                {
                    servers.Add(server);
                    slot.Server = null;
                }

                // Retired servers still serving a request go down with the rest.
                servers.AddRange(slot.Retired.Keys);
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
        var toStop = new List<ILlamaServer>(2);
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
                    toStop.Add(server);
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

        foreach (var server in toStop)
        {
            _ = BeginStop(server);
        }
    }

    private Dictionary<ILlamaServer, Task> SnapshotStopping()
    {
        lock (_lock)
        {
            return new Dictionary<ILlamaServer, Task>(_stopping);
        }
    }

    /// <summary>Stops <paramref name="server"/> gracefully in the background; it stays visible to kill/unload/dispose until it is gone.</summary>
    private Task BeginStop(ILlamaServer server)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_stopping.TryGetValue(server, out var existing))
            {
                return existing;
            }

            _stopping[server] = done.Task;
        }

        _ = RunStopAsync(server, done);
        return done.Task;
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

    private sealed record OptionsSnapshot(LocalLlmOptions Options, long At);

    private sealed class Slot(bool embedding)
    {
        /// <summary>Model file the running <see cref="Server"/> was started with.</summary>
        public string? RunningModelPath { get; set; }

        /// <summary><c>llama-server</c> binary the running <see cref="Server"/> was started from.</summary>
        public string? RunningServerPath { get; set; }

        /// <summary>Servers replaced after a model switch that still have requests in flight (value = requests left).</summary>
        public Dictionary<ILlamaServer, int> Retired { get; } = [];

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
