using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;
using Murmur.Intelligence.Llama;
using Murmur.Intelligence.Tests.Fakes;

namespace Murmur.Intelligence.Tests;

public sealed class LocalLlmTests : IAsyncLifetime
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "murmur-localllm-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly ConcurrentQueue<(LlamaServerOptions Options, FakeServerHandle Handle)> _started = new();
    private FakeOpenAiServer _http = null!;
    private TaskCompletionSource? _loadGate;
    private volatile bool _recording;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ServerPath => Path.Combine(_directory, "llama-server");

    private string ChatModel => Path.Combine(_directory, "chat.gguf");

    private string EmbeddingModel => Path.Combine(_directory, "embed.gguf");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ServerPath, "");
        File.WriteAllText(ChatModel, "");
        File.WriteAllText(EmbeddingModel, "");
        _http = new FakeOpenAiServer(async (request, response) =>
        {
            if (request.Path == "/v1/embeddings")
            {
                await FakeOpenAiServer.WriteJsonAsync(response, """{"data":[{"index":0,"embedding":[3,4]}]}""");
                return;
            }

            if (RequestGate is { } gate)
            {
                await gate.Task;
            }

            await FakeOpenAiServer.WriteJsonAsync(response,
                """{"choices":[{"index":0,"message":{"role":"assistant","content":"pong"},"finish_reason":"stop"}]}""");
        });
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _http.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    private TaskCompletionSource? RequestGate { get; set; }

    private LocalLlmOptions Options => new()
    {
        LlamaServerPath = ServerPath,
        ChatModelPath = ChatModel,
        EmbeddingModelPath = EmbeddingModel,
        Threads = 3,
        ContextSize = 4096,
        EmbeddingContextSize = 2048,
        IdleUnload = Idle,
    };

    private LocalLlm Create(LocalLlmOptions? options = null) =>
        new(options ?? Options, () => _recording, StartAsync, _time, httpClient: null);

    private async Task<ILlamaServer> StartAsync(LlamaServerOptions options, CancellationToken cancellationToken)
    {
        var handle = new FakeServerHandle(_http.BaseAddress);
        _started.Enqueue((options, handle));
        if (_loadGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return handle;
    }

    [Fact]
    public async Task Construction_StartsNothing_AndStatusIsUnloaded()
    {
        await using var llm = Create();

        Assert.Equal(LlmStatus.Unloaded, llm.Status);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task Status_NotInstalled_WhenServerOrBothModelsAreMissing()
    {
        await using var noServer = Create(Options with { LlamaServerPath = Path.Combine(_directory, "missing") });
        await using var noModels = Create(Options with { ChatModelPath = null, EmbeddingModelPath = Path.Combine(_directory, "missing.gguf") });
        await using var chatOnly = Create(Options with { EmbeddingModelPath = null });

        Assert.Equal(LlmStatus.NotInstalled, noServer.Status);
        Assert.Equal(LlmStatus.NotInstalled, noModels.Status);
        Assert.Equal(LlmStatus.Unloaded, chatOnly.Status);
        await Assert.ThrowsAsync<FileNotFoundException>(() => noServer.GetChatClientAsync(Ct).AsTask());
        await Assert.ThrowsAsync<FileNotFoundException>(() => chatOnly.GetEmbeddingGeneratorAsync(Ct).AsTask());
        Assert.Empty(_started);
    }

    [Fact]
    public async Task GetChatClient_StartsTheChatServerOnceAndServesRequests()
    {
        await using var llm = Create();

        var tasks = Enumerable.Range(0, 5).Select(_ => llm.GetChatClientAsync(Ct).AsTask()).ToArray();
        var clients = await Task.WhenAll(tasks);
        var response = await clients[0].GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], cancellationToken: Ct);

        Assert.Equal("pong", response.Text);
        Assert.Equal(LlmStatus.Ready, llm.Status);
        var (options, _) = Assert.Single(_started);
        Assert.Equal(ServerPath, options.ServerPath);
        Assert.Equal(ChatModel, options.ModelPath);
        Assert.False(options.Embedding);
        Assert.Equal(4096, options.ContextSize);
        Assert.Equal(3, options.Threads);
        Assert.Equal("chat", clients[0].GetService<ChatClientMetadata>()?.DefaultModelId);
    }

    [Fact]
    public async Task GetEmbeddingGenerator_StartsTheEmbeddingServerWithItsOwnSettings()
    {
        await using var llm = Create();

        var generator = await llm.GetEmbeddingGeneratorAsync(Ct);
        var embeddings = await generator.GenerateAsync(["x"], cancellationToken: Ct);

        Assert.Equal(0.6f, embeddings[0].Vector.Span[0], 5);
        var (options, _) = Assert.Single(_started);
        Assert.True(options.Embedding);
        Assert.Equal(EmbeddingModel, options.ModelPath);
        Assert.Equal(2048, options.ContextSize);
        Assert.Equal(["-b", "2048", "-ub", "2048"], options.ExtraArguments);
    }

    [Fact]
    public async Task Status_IsLoadingWhileTheServerStarts()
    {
        await using var llm = Create();
        _loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = llm.GetChatClientAsync(Ct).AsTask();
        await WaitUntilAsync(() => llm.Status == LlmStatus.Loading);
        _loadGate.SetResult();
        await pending;

        Assert.Equal(LlmStatus.Ready, llm.Status);
    }

    [Fact]
    public async Task RecordingGuard_ThrowsAndStartsNothing()
    {
        await using var llm = Create();
        _recording = true;

        var chat = await Assert.ThrowsAsync<AiPausedException>(() => llm.GetChatClientAsync(Ct).AsTask());
        await Assert.ThrowsAsync<AiPausedException>(() => llm.GetEmbeddingGeneratorAsync(Ct).AsTask());

        Assert.IsAssignableFrom<InvalidOperationException>(chat);
        Assert.Equal("AI is paused while recording.", chat.Message);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task RecordingGuard_AppliesToClientsObtainedEarlier()
    {
        await using var llm = Create();
        var client = await llm.GetChatClientAsync(Ct);
        _recording = true;

        await Assert.ThrowsAsync<AiPausedException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct));
    }

    [Fact]
    public async Task NotifyRecordingStarted_KillsRunningServersImmediately()
    {
        await using var llm = Create();
        await llm.GetChatClientAsync(Ct);
        await llm.GetEmbeddingGeneratorAsync(Ct);
        var handles = _started.Select(s => s.Handle).ToArray();

        _recording = true;
        llm.NotifyRecordingStarted();

        Assert.Equal(LlmStatus.Unloaded, llm.Status);
        Assert.All(handles, h => Assert.True(h.Killed));
        Assert.All(handles, h => Assert.False(h.Stopped));
    }

    [Fact]
    public async Task NotifyRecordingStarted_CancelsALoadInProgress()
    {
        await using var llm = Create();
        _loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = llm.GetChatClientAsync(Ct).AsTask();
        await WaitUntilAsync(() => llm.Status == LlmStatus.Loading);

        _recording = true;
        llm.NotifyRecordingStarted();

        await Assert.ThrowsAsync<AiPausedException>(() => pending);
        Assert.Equal(LlmStatus.Unloaded, llm.Status);
    }

    [Fact]
    public async Task ServerLoadedDuringRecording_IsDiscarded()
    {
        await using var llm = Create();
        _loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = llm.GetChatClientAsync(Ct).AsTask();
        await WaitUntilAsync(() => llm.Status == LlmStatus.Loading);

        _recording = true; // recorder forgot NotifyRecordingStarted
        _loadGate.SetResult();

        await Assert.ThrowsAsync<AiPausedException>(() => pending);
        Assert.Equal(LlmStatus.Unloaded, llm.Status);
        Assert.True(Assert.Single(_started).Handle.Killed);
    }

    [Fact]
    public async Task AfterRecording_TheModelLoadsAgain()
    {
        await using var llm = Create();
        var client = await llm.GetChatClientAsync(Ct);
        _recording = true;
        llm.NotifyRecordingStarted();
        _recording = false;

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct);

        Assert.Equal("pong", response.Text);
        Assert.Equal(2, _started.Count);
    }

    [Fact]
    public async Task IdleUnload_StopsTheServerAfterFiveIdleMinutes_AndTheClientRestartsIt()
    {
        await using var llm = Create();
        var client = await llm.GetChatClientAsync(Ct);
        var first = Assert.Single(_started).Handle;

        _time.Advance(Idle - TimeSpan.FromSeconds(1));
        Assert.Equal(LlmStatus.Ready, llm.Status);

        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => first.Disposed);
        Assert.True(first.Stopped);
        Assert.Equal(LlmStatus.Unloaded, llm.Status);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct);
        Assert.Equal("pong", response.Text);
        Assert.Equal(2, _started.Count);
        Assert.Equal(LlmStatus.Ready, llm.Status);
    }

    [Fact]
    public async Task IdleUnload_EachRequestResetsTheIdleTimer()
    {
        await using var llm = Create();
        var client = await llm.GetChatClientAsync(Ct);

        _time.Advance(TimeSpan.FromMinutes(4));
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct);
        _time.Advance(TimeSpan.FromMinutes(4));

        Assert.Equal(LlmStatus.Ready, llm.Status);
        _time.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => llm.Status == LlmStatus.Unloaded);
    }

    [Fact]
    public async Task IdleUnload_EmbeddingTrafficDoesNotKeepAnIdleChatServerLoaded()
    {
        await using var llm = Create();
        var client = await llm.GetChatClientAsync(Ct);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct);
        var generator = await llm.GetEmbeddingGeneratorAsync(Ct);
        var handles = _started.ToArray();
        Assert.Equal(2, handles.Length);
        var chat = Assert.Single(handles, h => !h.Options.Embedding).Handle;
        var embedding = Assert.Single(handles, h => h.Options.Embedding).Handle;

        // Embeddings used at t = 4, 8 and 12 min; the chat server was last used at t = 0.
        _time.Advance(TimeSpan.FromMinutes(4));
        await generator.GenerateAsync(["a"], cancellationToken: Ct);
        Assert.False(chat.Stopped);

        _time.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => chat.Disposed);
        Assert.True(chat.Stopped);
        Assert.False(embedding.Stopped);
        Assert.Equal(LlmStatus.Ready, llm.Status);

        _time.Advance(TimeSpan.FromMinutes(3));
        await generator.GenerateAsync(["b"], cancellationToken: Ct);
        _time.Advance(TimeSpan.FromMinutes(4));
        await generator.GenerateAsync(["c"], cancellationToken: Ct);
        Assert.False(embedding.Stopped);
        Assert.Equal(LlmStatus.Ready, llm.Status);

        // The embedding server unloads five minutes after its own last request.
        _time.Advance(Idle - TimeSpan.FromSeconds(1));
        Assert.False(embedding.Stopped);
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => embedding.Disposed);
        Assert.True(embedding.Stopped);
        Assert.Equal(LlmStatus.Unloaded, llm.Status);
        Assert.Equal(2, _started.Count);
    }

    [Fact]
    public async Task IdleUnload_ChatTrafficDoesNotPostponeTheEmbeddingUnload()
    {
        await using var llm = Create();
        var generator = await llm.GetEmbeddingGeneratorAsync(Ct);
        await generator.GenerateAsync(["a"], cancellationToken: Ct);
        _time.Advance(TimeSpan.FromMinutes(2));
        var client = await llm.GetChatClientAsync(Ct);
        var embedding = Assert.Single(_started, h => h.Options.Embedding).Handle;
        var chat = Assert.Single(_started, h => !h.Options.Embedding).Handle;

        _time.Advance(TimeSpan.FromMinutes(2));
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct);
        _time.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => embedding.Disposed);
        Assert.False(chat.Stopped);

        _time.Advance(TimeSpan.FromMinutes(4) - TimeSpan.FromSeconds(1));
        Assert.False(chat.Stopped);
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => chat.Disposed);
    }

    [Fact]
    public async Task IdleUnload_NeverStopsAServerWithARequestInFlight()
    {
        await using var llm = Create();
        var client = await llm.GetChatClientAsync(Ct);
        RequestGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var inFlight = client.GetResponseAsync([new ChatMessage(ChatRole.User, "slow")], cancellationToken: Ct);
        await WaitUntilAsync(() => _http.Requests.Count == 1);
        _time.Advance(Idle * 2);
        Assert.Equal(LlmStatus.Ready, llm.Status);

        RequestGate.SetResult();
        Assert.Equal("pong", (await inFlight).Text);
        _time.Advance(Idle);
        await WaitUntilAsync(() => llm.Status == LlmStatus.Unloaded);
        Assert.Single(_started);
    }

    [Fact]
    public async Task IdleUnload_BothServersIdleTogether_AreBothStoppedByOneTimerFire()
    {
        await using var llm = Create();
        await llm.GetChatClientAsync(Ct);
        await llm.GetEmbeddingGeneratorAsync(Ct);

        _time.Advance(Idle);
        await WaitUntilAsync(() => _started.All(h => h.Handle.Disposed));
        Assert.All(_started, h => Assert.True(h.Handle.Stopped));
        Assert.Equal(LlmStatus.Unloaded, llm.Status);
    }

    [Fact]
    public async Task IdleUnload_InfiniteIdleUnload_NeverStopsAServer()
    {
        await using var llm = Create(Options with { IdleUnload = Timeout.InfiniteTimeSpan });
        var client = await llm.GetChatClientAsync(Ct);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct);

        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(LlmStatus.Ready, llm.Status);
        Assert.False(Assert.Single(_started).Handle.Stopped);
    }

    [Fact]
    public async Task IdleUnload_TimerFiringAfterDispose_DoesNotStopTwiceOrThrow()
    {
        var llm = Create();
        await llm.GetChatClientAsync(Ct);
        var handle = Assert.Single(_started).Handle;
        await llm.DisposeAsync();

        _time.Advance(Idle * 2);
        await Task.Delay(50, Ct);
        Assert.False(handle.Stopped);
        Assert.True(handle.Disposed);
    }

    [Fact]
    public async Task UnloadAsync_StopsBothServersGracefully()
    {
        await using var llm = Create();
        await llm.GetChatClientAsync(Ct);
        await llm.GetEmbeddingGeneratorAsync(Ct);

        await llm.UnloadAsync(Ct);

        Assert.Equal(LlmStatus.Unloaded, llm.Status);
        Assert.All(_started, s => Assert.True(s.Handle.Stopped && s.Handle.Disposed));
    }

    [Fact]
    public async Task CrashedServer_IsRestartedOnTheNextRequest()
    {
        await using var llm = Create();
        var client = await llm.GetChatClientAsync(Ct);
        Assert.Single(_started).Handle.HasExited = true;

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct);

        Assert.Equal(2, _started.Count);
        Assert.True(_started.First().Handle.Disposed);
    }

    [Fact]
    public async Task Dispose_KillsServersAndRejectsFurtherUse()
    {
        var llm = Create();
        await llm.GetChatClientAsync(Ct);

        await llm.DisposeAsync();
        await llm.DisposeAsync();

        Assert.True(Assert.Single(_started).Handle.Killed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => llm.GetChatClientAsync(Ct).AsTask());
    }

    [Fact]
    public void FromEnvironment_ReadsVariablesWithModelsDirFallbacks()
    {
        var env = new Dictionary<string, string?> { ["MURMUR_MODELS_DIR"] = "/models" };
        var defaults = LocalLlmOptions.FromEnvironment(name => env.GetValueOrDefault(name));

        Assert.Equal(Path.Combine("/models", LocalLlmOptions.DefaultChatModelFileName), defaults.ChatModelPath);
        Assert.Equal(Path.Combine("/models", LocalLlmOptions.DefaultEmbeddingModelFileName), defaults.EmbeddingModelPath);
        Assert.Equal(LocalLlmOptions.DefaultServerPath("/models"), defaults.LlamaServerPath);
        Assert.Equal(8192, defaults.ContextSize);
        Assert.Equal(TimeSpan.FromMinutes(5), defaults.IdleUnload);

        env["MURMUR_LLM_MODEL"] = "/x/gemma.gguf";
        env["MURMUR_EMBED_MODEL"] = "/x/embed.gguf";
        env["MURMUR_LLAMA_SERVER"] = "/opt/llama/llama-server";
        var explicitPaths = LocalLlmOptions.FromEnvironment(name => env.GetValueOrDefault(name));

        Assert.Equal("/x/gemma.gguf", explicitPaths.ChatModelPath);
        Assert.Equal("/x/embed.gguf", explicitPaths.EmbeddingModelPath);
        Assert.Equal("/opt/llama/llama-server", explicitPaths.LlamaServerPath);
    }

    [Fact]
    public void FromEnvironment_WithoutVariables_UsesTheLocalAppDataModelsFolder()
    {
        var options = LocalLlmOptions.FromEnvironment(_ => null);

        Assert.StartsWith(LocalLlmOptions.DefaultModelsDirectory, options.ChatModelPath, StringComparison.Ordinal);
        Assert.EndsWith(OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server", options.LlamaServerPath, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("llama.cpp", ""), options.LlamaServerPath, StringComparison.Ordinal);
    }

    private LocalLlmOptions _live = new();

    private LocalLlm CreateLive() =>
        new(() => _live, () => _recording, StartAsync, _time, httpClient: null);

    private string SecondChatModel => Path.Combine(_directory, "chat-26b.gguf");

    [Fact]
    public async Task Live_InstallAfterStart_FlipsIsInstalledWithoutANewInstance()
    {
        var missing = Path.Combine(_directory, "later");
        _live = Options with
        {
            LlamaServerPath = Path.Combine(missing, "llama-server"),
            ChatModelPath = Path.Combine(missing, "chat.gguf"),
            EmbeddingModelPath = Path.Combine(missing, "embed.gguf"),
        };
        await using var llm = CreateLive();
        Assert.False(llm.IsInstalled);
        Assert.Equal(LlmStatus.NotInstalled, llm.Status);

        // Files appear at the paths the options already point to: visible at once.
        Directory.CreateDirectory(missing);
        await File.WriteAllTextAsync(_live.LlamaServerPath!, "", Ct);
        await File.WriteAllTextAsync(_live.ChatModelPath!, "", Ct);
        Assert.True(llm.IsInstalled);
        Assert.Equal(LlmStatus.Unloaded, llm.Status);

        // The first chat request starts the server found on disk.
        await llm.GetChatClientAsync(Ct);
        Assert.Equal(_live.ChatModelPath, Assert.Single(_started).Options.ModelPath);
    }

    [Fact]
    public async Task Live_ManifestChange_IsPickedUpAfterTheRefreshInterval()
    {
        _live = Options with { ChatModelPath = null, EmbeddingModelPath = null };
        await using var llm = CreateLive();
        Assert.Equal(LlmStatus.NotInstalled, llm.Status);

        _live = Options;
        Assert.Equal(LlmStatus.NotInstalled, llm.Status); // cached for a moment
        _time.Advance(LocalLlm.OptionsRefreshInterval + TimeSpan.FromMilliseconds(1));

        Assert.Equal(LlmStatus.Unloaded, llm.Status);
    }

    [Fact]
    public async Task Live_Refresh_PicksUpNewPathsImmediately()
    {
        _live = Options with { ChatModelPath = null, EmbeddingModelPath = null };
        await using var llm = CreateLive();
        Assert.Equal(LlmStatus.NotInstalled, llm.Status);

        _live = Options;
        llm.Refresh();

        Assert.Equal(LlmStatus.Unloaded, llm.Status);
    }

    [Fact]
    public async Task Live_ARequestSeesNewPathsEvenInsideTheRefreshInterval()
    {
        _live = Options with { ChatModelPath = null };
        await using var llm = CreateLive();
        Assert.Equal(LlmStatus.Unloaded, llm.Status);

        _live = Options;
        var client = await llm.GetChatClientAsync(Ct);

        Assert.Equal(ChatModel, Assert.Single(_started).Options.ModelPath);
        Assert.Equal("chat", client.GetService<ChatClientMetadata>()?.DefaultModelId);
    }

    [Fact]
    public async Task Live_SwitchingTheChatModel_RestartsTheChatServerOnTheNextRequest()
    {
        await File.WriteAllTextAsync(SecondChatModel, "", Ct);
        _live = Options;
        await using var llm = CreateLive();
        var client = await llm.GetChatClientAsync(Ct);
        await llm.GetEmbeddingGeneratorAsync(Ct);
        var first = _started.Single(s => !s.Options.Embedding).Handle;
        var embedding = _started.Single(s => s.Options.Embedding).Handle;

        _live = Options with { ChatModelPath = SecondChatModel };
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct);

        Assert.Equal("pong", response.Text);
        Assert.Equal(3, _started.Count);
        Assert.Equal(SecondChatModel, _started.Last().Options.ModelPath);
        Assert.False(_started.Last().Options.Embedding);
        await WaitUntilAsync(() => first.Disposed);
        Assert.True(first.Stopped);
        Assert.False(embedding.Stopped);
        Assert.Equal(LlmStatus.Ready, llm.Status);
        Assert.Equal("chat-26b", client.GetService<ChatClientMetadata>()?.DefaultModelId);
    }

    [Fact]
    public async Task Live_SwitchingTheChatModel_LetsAnInFlightRequestFinishOnTheOldServer()
    {
        await File.WriteAllTextAsync(SecondChatModel, "", Ct);
        _live = Options;
        await using var llm = CreateLive();
        var client = await llm.GetChatClientAsync(Ct);
        var first = Assert.Single(_started).Handle;
        RequestGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct);
        await WaitUntilAsync(() => !_http.Requests.IsEmpty);

        _live = Options with { ChatModelPath = SecondChatModel };
        await llm.GetChatClientAsync(Ct); // starts the new server

        Assert.Equal(2, _started.Count);
        Assert.False(first.Stopped);
        Assert.False(first.Killed);
        Assert.False(inFlight.IsCompleted);

        RequestGate.SetResult();
        var response = await inFlight;

        Assert.Equal("pong", response.Text);
        await WaitUntilAsync(() => first.Disposed);
        Assert.True(first.Stopped);
        Assert.False(_started.Last().Handle.Stopped);
        Assert.Equal(LlmStatus.Ready, llm.Status);
    }

    [Fact]
    public async Task Live_SwitchingTheEmbeddingModel_RestartsOnlyTheEmbeddingServer()
    {
        var second = Path.Combine(_directory, "embed2.gguf");
        await File.WriteAllTextAsync(second, "", Ct);
        _live = Options;
        await using var llm = CreateLive();
        await llm.GetChatClientAsync(Ct);
        var generator = await llm.GetEmbeddingGeneratorAsync(Ct);
        var chat = _started.Single(s => !s.Options.Embedding).Handle;
        var firstEmbedding = _started.Single(s => s.Options.Embedding).Handle;

        _live = Options with { EmbeddingModelPath = second };
        await generator.GenerateAsync(["x"], cancellationToken: Ct);

        Assert.Equal(3, _started.Count);
        Assert.Equal(second, _started.Last().Options.ModelPath);
        Assert.True(_started.Last().Options.Embedding);
        await WaitUntilAsync(() => firstEmbedding.Disposed);
        Assert.False(chat.Stopped);
    }

    [Fact]
    public async Task Live_RemovingTheFiles_FlipsToNotInstalled_AndRequestsFail()
    {
        _live = Options;
        await using var llm = CreateLive();
        Assert.Equal(LlmStatus.Unloaded, llm.Status);

        File.Delete(ChatModel);
        File.Delete(EmbeddingModel);

        Assert.Equal(LlmStatus.NotInstalled, llm.Status);
        await Assert.ThrowsAsync<FileNotFoundException>(() => llm.GetChatClientAsync(Ct).AsTask());
        Assert.Empty(_started);
    }

    [Fact]
    public async Task Live_RemovedModelWithARunningServer_IsRetiredByTheNextRequest()
    {
        _live = Options;
        await using var llm = CreateLive();
        var client = await llm.GetChatClientAsync(Ct);
        var first = Assert.Single(_started).Handle;

        File.Delete(ChatModel);
        _live = Options with { ChatModelPath = null };

        await Assert.ThrowsAsync<FileNotFoundException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct));
        await WaitUntilAsync(() => first.Disposed);
        Assert.True(first.Stopped);
    }

    [Fact]
    public async Task Live_RecordingStartsWhileTheOldServerStops_TheNewModelIsNotLoaded()
    {
        await File.WriteAllTextAsync(SecondChatModel, "", Ct);
        _live = Options;
        await using var llm = CreateLive();
        var client = await llm.GetChatClientAsync(Ct);
        var first = Assert.Single(_started).Handle;
        first.StopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _live = Options with { ChatModelPath = SecondChatModel };
        var pending = client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct);
        await WaitUntilAsync(() => first.Stopped);

        _recording = true;
        llm.NotifyRecordingStarted();

        await Assert.ThrowsAsync<AiPausedException>(() => pending);
        Assert.Single(_started);
        Assert.True(first.Killed);
        await WaitUntilAsync(() => first.Disposed);
        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public async Task Live_RecordingGuardTripsAfterTheNewServerStarts_KillsTheRetiredServerToo()
    {
        await File.WriteAllTextAsync(SecondChatModel, "", Ct);
        _live = Options;
        await using var llm = CreateLive();
        var client = await llm.GetChatClientAsync(Ct);
        var old = Assert.Single(_started).Handle;
        RequestGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct);
        await WaitUntilAsync(() => !_http.Requests.IsEmpty);

        _live = Options with { ChatModelPath = SecondChatModel };
        _loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = llm.GetChatClientAsync(Ct).AsTask();
        await WaitUntilAsync(() => _started.Count == 2);

        _recording = true; // recorder forgot NotifyRecordingStarted
        _loadGate.SetResult();

        await Assert.ThrowsAsync<AiPausedException>(() => pending);
        var fresh = _started.Last().Handle;
        Assert.True(fresh.Killed);
        Assert.True(old.Killed);
        await WaitUntilAsync(() => old.Disposed && fresh.Disposed);
        Assert.Equal(1, old.DisposeCount);
        Assert.Equal(1, fresh.DisposeCount);
        Assert.False(old.Stopped);

        RequestGate.SetResult();
        try
        {
            await inFlight;
        }
        catch (Exception)
        {
            // The retired server was killed under it; only the process cleanup matters here.
        }
    }

    [Fact]
    public async Task Stopping_UnloadAsyncWaitsForAServerThatIsStillStopping()
    {
        _live = Options;
        await using var llm = CreateLive();
        await llm.GetChatClientAsync(Ct);
        var first = Assert.Single(_started).Handle;
        first.StopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _time.Advance(Idle);
        await WaitUntilAsync(() => first.Stopped);

        var unload = llm.UnloadAsync(Ct).AsTask();
        await Task.Delay(50, Ct);
        Assert.False(unload.IsCompleted);

        first.StopGate.SetResult();
        await unload;

        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task Stopping_NotifyRecordingStartedKillsAServerThatIsStillStopping()
    {
        _live = Options;
        await using var llm = CreateLive();
        await llm.GetChatClientAsync(Ct);
        var first = Assert.Single(_started).Handle;
        first.StopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _time.Advance(Idle);
        await WaitUntilAsync(() => first.Stopped);

        _recording = true;
        llm.NotifyRecordingStarted();

        Assert.True(first.Killed);
        await WaitUntilAsync(() => first.Disposed);
        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public async Task Stopping_DisposeAsyncKillsAndWaitsForAServerThatIsStillStopping()
    {
        _live = Options;
        var llm = CreateLive();
        await llm.GetChatClientAsync(Ct);
        var first = Assert.Single(_started).Handle;
        first.StopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _time.Advance(Idle);
        await WaitUntilAsync(() => first.Stopped);

        await llm.DisposeAsync();

        Assert.True(first.Killed);
        Assert.Equal(1, first.DisposeCount);
    }

    [Theory]
    [InlineData("notify")]
    [InlineData("unload")]
    [InlineData("dispose")]
    public async Task EveryStartedServer_IsStoppedOrKilledAndDisposedExactlyOnce(string how)
    {
        await File.WriteAllTextAsync(SecondChatModel, "", Ct);
        _live = Options;
        var llm = CreateLive();
        var client = await llm.GetChatClientAsync(Ct);
        await llm.GetEmbeddingGeneratorAsync(Ct);

        // A retired chat server with a request in flight, a fresh chat server, and the embedding server.
        RequestGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct);
        await WaitUntilAsync(() => !_http.Requests.IsEmpty);
        _live = Options with { ChatModelPath = SecondChatModel };
        await llm.GetChatClientAsync(Ct);
        Assert.Equal(3, _started.Count);

        switch (how)
        {
            case "notify":
                _recording = true;
                llm.NotifyRecordingStarted();
                break;
            case "unload":
                await llm.UnloadAsync(Ct);
                break;
            default:
                await llm.DisposeAsync();
                break;
        }

        await WaitUntilAsync(() => _started.All(s => s.Handle.Disposed));
        Assert.All(_started, s =>
        {
            Assert.Equal(1, s.Handle.DisposeCount);
            Assert.True(s.Handle.Stopped || s.Handle.Killed);
        });

        // Late completion of the request that was in flight must not stop or dispose anything again.
        RequestGate.SetResult();
        try
        {
            await inFlight;
        }
        catch (Exception)
        {
            // The server went away under it.
        }

        await Task.Delay(50, Ct);
        Assert.All(_started, s => Assert.Equal(1, s.Handle.DisposeCount));
        if (how != "dispose")
        {
            await llm.DisposeAsync();
        }
    }

    [Fact]
    public async Task IsUsingModel_TrueForLiveRetiredAndStoppingServers_FalseOtherwise()
    {
        await File.WriteAllTextAsync(SecondChatModel, "", Ct);
        _live = Options;
        await using var llm = CreateLive();
        Assert.False(llm.IsUsingModel(ChatModel));

        var client = await llm.GetChatClientAsync(Ct);
        Assert.True(llm.IsUsingModel(ChatModel));
        Assert.False(llm.IsUsingModel(EmbeddingModel));
        Assert.False(llm.IsUsingModel(SecondChatModel));

        // Switched in the manifest, but the old server is still serving a request: it still holds the old file.
        RequestGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: Ct);
        await WaitUntilAsync(() => !_http.Requests.IsEmpty);
        _live = Options with { ChatModelPath = SecondChatModel };
        Assert.True(llm.IsUsingModel(ChatModel)); // before the next request even restarts anything
        await llm.GetChatClientAsync(Ct);
        Assert.True(llm.IsUsingModel(ChatModel));
        Assert.True(llm.IsUsingModel(SecondChatModel));

        // The old server is released once its request is done and it has stopped.
        var old = _started.First().Handle;
        old.StopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestGate.SetResult();
        await inFlight;
        await WaitUntilAsync(() => old.Stopped);
        Assert.True(llm.IsUsingModel(ChatModel)); // stopping, not gone yet

        old.StopGate.SetResult();
        await WaitUntilAsync(() => old.Disposed);
        Assert.False(llm.IsUsingModel(ChatModel));

        await llm.UnloadAsync(Ct);
        Assert.False(llm.IsUsingModel(SecondChatModel));
    }

    [Fact]
    public async Task UnloadAsync_ServersAreTrackedAsStopping_SoARecordingKillsThem()
    {
        _live = Options;
        await using var llm = CreateLive();
        await llm.GetChatClientAsync(Ct);
        var first = Assert.Single(_started).Handle;
        first.StopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var unload = llm.UnloadAsync(Ct).AsTask();
        await WaitUntilAsync(() => first.Stopped);
        Assert.True(llm.IsUsingModel(ChatModel));
        Assert.False(unload.IsCompleted);

        _recording = true;
        llm.NotifyRecordingStarted();
        await unload;

        Assert.True(first.Killed);
        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public async Task UnloadAsync_CancellationStopsWaiting_ButTheServerKeepsStopping()
    {
        _live = Options;
        await using var llm = CreateLive();
        await llm.GetChatClientAsync(Ct);
        var first = Assert.Single(_started).Handle;
        first.StopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var unload = llm.UnloadAsync(cts.Token).AsTask();
        await WaitUntilAsync(() => first.Stopped);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unload);
        first.StopGate.SetResult();
        await WaitUntilAsync(() => first.Disposed);
        Assert.Equal(1, first.DisposeCount);
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

    private sealed class FakeServerHandle(Uri baseAddress) : ILlamaServer
    {
        public Uri BaseAddress { get; } = baseAddress;

        public bool HasExited { get; set; }

        public bool Stopped { get; private set; }

        public bool Killed { get; private set; }

        public bool Disposed => DisposeCount > 0;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        /// <summary>When set, a graceful stop waits for it (like a slow process exit); <see cref="Kill"/> releases it.</summary>
        public TaskCompletionSource? StopGate { get; set; }

        private int _disposeCount;

        public async ValueTask StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            if (StopGate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            HasExited = true;
        }

        public void Kill()
        {
            Killed = true;
            HasExited = true;
            StopGate?.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>LocalLlm driving the real <see cref="LlamaServerProcess"/> against the python stand-in server.</summary>
public sealed class LocalLlmProcessTests
{
    [Fact]
    public async Task EndToEnd_StartsServesAndUnloadsRealChildProcesses()
    {
        Assert.SkipUnless(FakeLlamaServerScript.IsSupported, "Needs python3 on a Unix OS.");
        var ct = TestContext.Current.CancellationToken;
        using var script = new FakeLlamaServerScript(unhealthyPolls: 1);
        var options = new LocalLlmOptions
        {
            LlamaServerPath = script.ServerPath,
            ChatModelPath = script.ModelPath,
            EmbeddingModelPath = script.EmbeddingModelPath,
            Threads = 1,
        };
        await using var llm = new LocalLlm(options, () => false);

        var chat = await llm.GetChatClientAsync(ct);
        var answer = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "привіт")], cancellationToken: ct);
        var generator = await llm.GetEmbeddingGeneratorAsync(ct);
        var embeddings = await generator.GenerateAsync(["a", "b"], cancellationToken: ct);

        Assert.Equal("echo: привіт", answer.Text);
        Assert.Equal(2, embeddings.Count);
        Assert.Equal(0.8f, embeddings[1].Vector.Span[1], 5);
        Assert.Equal(LlmStatus.Ready, llm.Status);
        Assert.Contains("--embedding", script.ReadArguments()); // the embedding server started last

        await llm.UnloadAsync(ct);

        Assert.Equal(LlmStatus.Unloaded, llm.Status);
        Assert.True(script.WaitForProcessGone(TimeSpan.FromSeconds(5)));
    }
}
