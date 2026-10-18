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
public sealed class LocalLlm : ILlm
{
    private readonly LocalLlmOptions _options;
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
    private bool _disposed;

    // The shared idle timer is armed for the earliest due slot; guarded by _lock.
    private bool _idleArmed;
    private long _idleArmedAt;
    private TimeSpan _idleArmedDelay;

    /// <param name="options">Model and server paths.</param>
    /// <param name="isRecordingActive">Returns <c>true</c> while a recording is in progress.</param>
    public LocalLlm(LocalLlmOptions options, Func<bool> isRecordingActive)
        : this(options, isRecordingActive, StartProcessAsync, TimeProvider.System, httpClient: null)
    {
    }

    internal LocalLlm(LocalLlmOptions options, Func<bool> isRecordingActive, ServerStarter starter, TimeProvider time, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(isRecordingActive);
        ArgumentNullException.ThrowIfNull(starter);
        ArgumentNullException.ThrowIfNull(time);
        _options = options;
        _isRecordingActive = isRecordingActive;
        _starter = starter;
        _time = time;
        _http = httpClient ?? OpenAiHttp.CreateDefaultClient();
        _chat = new Slot(options.ChatModelPath, embedding: false);
        _embedding = new Slot(options.EmbeddingModelPath, embedding: true);
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
    public bool IsInstalled =>
        FileExists(_options.LlamaServerPath) && (FileExists(_options.ChatModelPath) || FileExists(_options.EmbeddingModelPath));

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
    }

    public async ValueTask UnloadAsync(CancellationToken cancellationToken)
    {
        foreach (var server in Detach(_chat, _embedding))
        {
            await StopQuietlyAsync(server, cancellationToken).ConfigureAwait(false);
        }
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

        _http.Dispose();
    }

    private static async Task<ILlamaServer> StartProcessAsync(LlamaServerOptions options, CancellationToken cancellationToken) =>
        await LlamaServerProcess.StartAsync(options, cancellationToken).ConfigureAwait(false);

    private static bool FileExists(string? path) => !string.IsNullOrEmpty(path) && File.Exists(path);

    private static string? ModelId(string? path) => path is null ? null : Path.GetFileNameWithoutExtension(path);

    private LlamaServerOptions CreateServerOptions(Slot slot)
    {
        var serverPath = _options.LlamaServerPath;
        if (!FileExists(serverPath))
        {
            throw new FileNotFoundException("llama-server is not installed.", serverPath);
        }

        if (!FileExists(slot.ModelPath))
        {
            throw new FileNotFoundException(
                slot.IsEmbedding ? "The embedding model is not installed." : "The chat model is not installed.", slot.ModelPath);
        }

        return new LlamaServerOptions
        {
            ServerPath = serverPath!,
            ModelPath = slot.ModelPath!,
            Threads = _options.Threads,
            ContextSize = slot.IsEmbedding ? _options.EmbeddingContextSize : _options.ContextSize,
            Embedding = slot.IsEmbedding,
            ExtraArguments = slot.IsEmbedding ? _options.EmbeddingExtraArguments : _options.ChatExtraArguments,
            StartupTimeout = _options.StartupTimeout,
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
        await AcquireAsync(slot, cancellationToken).ConfigureAwait(false);
        Release(slot);
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

    private void Release(Slot slot)
    {
        lock (_lock)
        {
            slot.InFlight--;
            slot.LastUse = _time.GetTimestamp();
            if (!_disposed && slot.InFlight == 0)
            {
                // Never postpone a pending (earlier) fire: it may be due for the other slot.
                // OnIdleTimer recomputes every slot's due time and re-arms for the next one.
                ArmIdleTimerIfEarlier(_options.IdleUnload, slot.LastUse);
            }
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
        lock (_lock)
        {
            if (slot.Server is { HasExited: false } running)
            {
                return running;
            }
        }

        var serverOptions = CreateServerOptions(slot);
        await slot.StartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            ILlamaServer? dead;
            CancellationToken lifetime;
            lock (_lock)
            {
                if (slot.Server is { HasExited: false } running)
                {
                    return running;
                }

                dead = slot.Server;
                slot.Server = null;
                slot.Loading = true;
                lifetime = slot.Lifetime.Token;
            }

            if (dead is not null)
            {
                await DisposeQuietlyAsync(dead).ConfigureAwait(false);
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
                    slot.LastUse = _time.GetTimestamp();
                }
            }

            if (!accepted || _isRecordingActive())
            {
                if (accepted)
                {
                    Detach(slot);
                }

                server.Kill();
                await DisposeQuietlyAsync(server).ConfigureAwait(false);
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
                if (slot.InFlight == 0 && idle >= _options.IdleUnload)
                {
                    toStop.Add(server);
                    slot.Server = null;
                    continue;
                }

                var due = slot.InFlight > 0 ? _options.IdleUnload : _options.IdleUnload - idle;
                nextDue = nextDue is { } current && current < due ? current : due;
            }

            if (nextDue is { } next)
            {
                ArmIdleTimerIfEarlier(next, _time.GetTimestamp());
            }
        }

        foreach (var server in toStop)
        {
            _ = StopQuietlyAsync(server, CancellationToken.None);
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

    private sealed class Slot(string? modelPath, bool embedding)
    {
        public string? ModelPath { get; } = modelPath;

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
        private readonly ChatClientMetadata _metadata = new("llama.cpp", null, ModelId(owner._options.ChatModelPath));

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var server = await owner.AcquireAsync(owner._chat, cancellationToken).ConfigureAwait(false);
            try
            {
                using var client = new LlamaChatClient(server.BaseAddress, owner._http, _metadata.DefaultModelId);
                return await client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                owner.Release(owner._chat);
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
                using var client = new LlamaChatClient(server.BaseAddress, owner._http, _metadata.DefaultModelId);
                await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                {
                    yield return update;
                }
            }
            finally
            {
                owner.Release(owner._chat);
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
                return _metadata;
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
        private readonly EmbeddingGeneratorMetadata _metadata = new("llama.cpp", null, ModelId(owner._options.EmbeddingModelPath));

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var server = await owner.AcquireAsync(owner._embedding, cancellationToken).ConfigureAwait(false);
            try
            {
                using var generator = new LlamaEmbeddingGenerator(
                    server.BaseAddress, owner._http, _metadata.DefaultModelId, owner._options.EmbeddingBatchSize);
                return await generator.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                owner.Release(owner._embedding);
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
                return _metadata;
            }

            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Owned by LocalLlm.
        }
    }
}
