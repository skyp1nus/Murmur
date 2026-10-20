using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;
using Murmur.Core.Settings;
using Murmur.Intelligence.Llama;
using Murmur.Intelligence.Tests.Fakes;

namespace Murmur.Intelligence.Tests;

/// <summary>GPU builds of llama-server: which binary and arguments, "needs download", fallback to the CPU build.</summary>
public sealed class LocalLlmAccelerationTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "murmur-accel-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly ConcurrentQueue<LlamaServerOptions> _started = new();
    private readonly ConcurrentQueue<Handle> _handles = new();
    private FakeOpenAiServer _http = null!;
    private volatile bool _recording;
    private Func<LlamaServerOptions, Exception?> _failure = _ => null;
    private volatile TaskCompletionSource? _requestGate;
    private int _requestsSeen;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CpuServer => Path.Combine(_directory, "llama.cpp", "llama-server");

    private string VulkanServer => Path.Combine(_directory, "llama.cpp-vulkan", "llama-server");

    private string CudaServer => Path.Combine(_directory, "llama.cpp-cuda", "llama-server");

    private string ChatModel => Path.Combine(_directory, "chat.gguf");

    private string EmbeddingModel => Path.Combine(_directory, "embed.gguf");

    public ValueTask InitializeAsync()
    {
        foreach (var server in (string[])[CpuServer, VulkanServer, CudaServer])
        {
            Directory.CreateDirectory(Path.GetDirectoryName(server)!);
            File.WriteAllText(server, "");
        }

        File.WriteAllText(ChatModel, "");
        File.WriteAllText(EmbeddingModel, "");
        _http = new FakeOpenAiServer(async (request, response) =>
        {
            if (request.Path == "/v1/embeddings")
            {
                await FakeOpenAiServer.WriteJsonAsync(response, """{"data":[{"index":0,"embedding":[3,4]}]}""");
                return;
            }

            Interlocked.Increment(ref _requestsSeen);
            if (_requestGate is { } gate)
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

    private LocalLlmOptions Options(AiAcceleration acceleration) => new()
    {
        LlamaServerPath = CpuServer,
        Acceleration = acceleration,
        AcceleratedServerPath = acceleration switch
        {
            AiAcceleration.Vulkan => VulkanServer,
            AiAcceleration.Cuda => CudaServer,
            _ => null,
        },
        ChatModelPath = ChatModel,
        EmbeddingModelPath = EmbeddingModel,
        Threads = 3,
    };

    private LocalLlm Create(Func<LocalLlmOptions> options) => new(options, () => _recording, StartAsync, _time, httpClient: null);

    private LocalLlm Create(LocalLlmOptions options) => Create(() => options);

    private Task<ILlamaServer> StartAsync(LlamaServerOptions options, CancellationToken cancellationToken)
    {
        _started.Enqueue(options);
        if (_failure(options) is { } error)
        {
            return Task.FromException<ILlamaServer>(error);
        }

        var handle = new Handle(_http.BaseAddress);
        _handles.Enqueue(handle);
        return Task.FromResult<ILlamaServer>(handle);
    }

    [Fact]
    public async Task Cpu_UsesTheCpuBuildWithoutGpuArguments()
    {
        await using var llm = Create(Options(AiAcceleration.Cpu));

        await llm.GetChatClientAsync(Ct);

        var started = Assert.Single(_started);
        Assert.Equal(CpuServer, started.ServerPath);
        Assert.Empty(started.ExtraArguments);
        Assert.Equal(new AccelerationStatus(AiAcceleration.Cpu, AiAcceleration.Cpu, AccelerationFallback.None), llm.Current);
    }

    [Theory]
    [InlineData(AiAcceleration.Vulkan)]
    [InlineData(AiAcceleration.Cuda)]
    public async Task Gpu_ChatServerRunsFromTheGpuBuildWithNgl99(AiAcceleration acceleration)
    {
        await using var llm = Create(Options(acceleration));

        var chat = await llm.GetChatClientAsync(Ct);
        var answer = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], cancellationToken: Ct);

        Assert.Equal("pong", answer.Text);
        var started = Assert.Single(_started);
        Assert.Equal(acceleration == AiAcceleration.Vulkan ? VulkanServer : CudaServer, started.ServerPath);
        Assert.Equal(["-ngl", "99"], started.ExtraArguments);
        Assert.False(started.Embedding);
        Assert.Equal(new AccelerationStatus(acceleration, acceleration, AccelerationFallback.None), llm.Current);
    }

    [Fact]
    public async Task Gpu_UserExtraArgumentsComeAfterTheGpuOnes_SoTheyCanOverrideThem()
    {
        await using var llm = Create(Options(AiAcceleration.Vulkan) with { ChatExtraArguments = ["-ngl", "20", "--no-mmap"] });

        await llm.GetChatClientAsync(Ct);

        Assert.Equal(["-ngl", "99", "-ngl", "20", "--no-mmap"], Assert.Single(_started).ExtraArguments);
    }

    [Fact]
    public async Task Gpu_EmbeddingServerStaysOnTheCpuBuild()
    {
        await using var llm = Create(Options(AiAcceleration.Cuda));

        await llm.GetEmbeddingGeneratorAsync(Ct);

        var started = Assert.Single(_started);
        Assert.True(started.Embedding);
        Assert.Equal(CpuServer, started.ServerPath);
        Assert.Equal(["-b", "2048", "-ub", "2048"], started.ExtraArguments);
    }

    [Fact]
    public async Task Gpu_EmbeddingServerCanBeAcceleratedOnRequest()
    {
        await using var llm = Create(Options(AiAcceleration.Vulkan) with { AccelerateEmbeddings = true });

        await llm.GetEmbeddingGeneratorAsync(Ct);

        var started = Assert.Single(_started);
        Assert.Equal(VulkanServer, started.ServerPath);
        Assert.Equal(["-ngl", "99", "-b", "2048", "-ub", "2048"], started.ExtraArguments);
    }

    [Fact]
    public async Task Gpu_NotInstalled_ReportsNeedsDownload_AndTheCpuBuildServesMeanwhile()
    {
        File.Delete(VulkanServer);
        await using var llm = Create(Options(AiAcceleration.Vulkan));

        var before = llm.Current;
        await llm.GetChatClientAsync(Ct);

        Assert.Equal(new AccelerationStatus(AiAcceleration.Vulkan, AiAcceleration.Cpu, AccelerationFallback.RuntimeNotInstalled), before);
        Assert.True(before.NeedsDownload);
        var started = Assert.Single(_started);
        Assert.Equal(CpuServer, started.ServerPath);
        Assert.Empty(started.ExtraArguments);
        Assert.True(llm.Current.NeedsDownload);
    }

    [Fact]
    public async Task Gpu_NotInstalled_NothingIsStartedFromTheMissingPath()
    {
        File.Delete(CudaServer);
        await using var llm = Create(Options(AiAcceleration.Cuda));

        await llm.GetChatClientAsync(Ct);
        await llm.GetEmbeddingGeneratorAsync(Ct);

        Assert.All(_started, s => Assert.Equal(CpuServer, s.ServerPath));
    }

    [Fact]
    public async Task Gpu_FailingToStart_FallsBackToTheCpuBuildAndSaysWhy()
    {
        _failure = o => o.ServerPath == VulkanServer
            ? new LlamaServerException("llama-server exited with code 3 before it became healthy.", "ggml_vulkan: No devices found.\nllama_model_load: error loading model", 3)
            : null;
        await using var llm = Create(Options(AiAcceleration.Vulkan));
        var raised = new List<AccelerationStatus>();
        llm.Changed += raised.Add;

        var chat = await llm.GetChatClientAsync(Ct);
        var answer = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], cancellationToken: Ct);

        Assert.Equal("pong", answer.Text);
        Assert.Equal([VulkanServer, CpuServer], _started.Select(s => s.ServerPath));
        Assert.Equal(["-ngl", "99"], _started.First().ExtraArguments);
        Assert.Empty(_started.Last().ExtraArguments);
        var expected = new AccelerationStatus(AiAcceleration.Vulkan, AiAcceleration.Cpu, AccelerationFallback.StartFailed, "llama_model_load: error loading model");
        Assert.Equal(expected, llm.Current);
        Assert.Equal([expected], raised);
        Assert.False(llm.Current.NeedsDownload);
    }

    [Fact]
    public async Task IsTheAccelerationStatusSource_AndRaisesChangedWhenTheStatusChanges()
    {
        var choice = AiAcceleration.Cpu;
        File.Delete(CudaServer);
        await using var llm = Create(() => Options(choice));
        IAccelerationStatusSource source = llm;
        var raised = new List<AccelerationStatus>();
        source.Changed += raised.Add;
        Assert.Equal(AccelerationFallback.None, source.Current.Fallback); // the first read is the baseline: no event

        choice = AiAcceleration.Cuda;
        _time.Advance(LocalLlm.OptionsRefreshInterval);
        Assert.True(source.Current.NeedsDownload);
        Assert.True(source.Current.NeedsDownload); // reading again is not a change

        File.WriteAllText(CudaServer, ""); // the runtime was installed
        _time.Advance(LocalLlm.OptionsRefreshInterval);
        llm.Refresh();

        Assert.Equal(
            [
                new AccelerationStatus(AiAcceleration.Cuda, AiAcceleration.Cpu, AccelerationFallback.RuntimeNotInstalled),
                new AccelerationStatus(AiAcceleration.Cuda, AiAcceleration.Cuda, AccelerationFallback.None),
            ],
            raised);
    }

    [Fact]
    public async Task Gpu_AfterAFailure_TheNextLoadsGoStraightToTheCpuBuild()
    {
        _failure = o => o.ServerPath == CudaServer ? new TimeoutException("never healthy") : null;
        await using var llm = Create(Options(AiAcceleration.Cuda));
        await llm.GetChatClientAsync(Ct);
        llm.NotifyRecordingStarted(); // unload so the next request has to start a server again
        while (_handles.TryDequeue(out _))
        {
        }

        await llm.GetChatClientAsync(Ct);

        Assert.Equal([CudaServer, CpuServer, CpuServer], _started.Select(s => s.ServerPath));
        Assert.Equal(AccelerationFallback.StartFailed, llm.Current.Fallback);
        Assert.Equal("llama-server did not become healthy in time.", llm.Current.Detail);
    }

    [Fact]
    public async Task Gpu_AfterAFailure_TheRunningCpuFallbackIsNotRestartedByEveryRequest()
    {
        _failure = o => o.ServerPath == VulkanServer ? new LlamaServerException("exit", "boom", 1) : null;
        await using var llm = Create(Options(AiAcceleration.Vulkan));

        var chat = await llm.GetChatClientAsync(Ct);
        for (var i = 0; i < 3; i++)
        {
            await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], cancellationToken: Ct);
        }

        Assert.Equal(2, _started.Count);
        Assert.Single(_handles);
    }

    [Fact]
    public async Task Gpu_ChoosingTheGpuAgainAfterTheCpuIsTheRetry()
    {
        var choice = AiAcceleration.Vulkan;
        var broken = true;
        _failure = o => o.ServerPath == VulkanServer && broken ? new LlamaServerException("exit", "driver too old", 1) : null;
        await using var llm = Create(() => Options(choice));
        await llm.GetChatClientAsync(Ct);
        Assert.Equal(AccelerationFallback.StartFailed, llm.Current.Fallback);

        choice = AiAcceleration.Cpu;
        _time.Advance(LocalLlm.OptionsRefreshInterval);
        Assert.Equal(AccelerationFallback.None, llm.Current.Fallback);
        await llm.GetChatClientAsync(Ct);

        broken = false;
        choice = AiAcceleration.Vulkan;
        _time.Advance(LocalLlm.OptionsRefreshInterval);
        Assert.Equal(AccelerationFallback.None, llm.Current.Fallback);
        await llm.GetChatClientAsync(Ct);

        Assert.Equal(VulkanServer, _started.Last().ServerPath);
        Assert.Equal(["-ngl", "99"], _started.Last().ExtraArguments);
    }

    [Fact]
    public async Task Gpu_DoesNotFallBack_WhenTheLoadIsCancelled()
    {
        _failure = o => o.ServerPath == VulkanServer ? new OperationCanceledException() : null;
        await using var llm = Create(Options(AiAcceleration.Vulkan));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => llm.GetChatClientAsync(Ct).AsTask());

        Assert.Equal([VulkanServer], _started.Select(s => s.ServerPath));
        Assert.Equal(AccelerationFallback.None, llm.Current.Fallback);
    }

    [Fact]
    public async Task Gpu_NothingStartsDuringARecording_NotEvenTheFallback()
    {
        // The recording starts while the GPU server is failing: no CPU server may load.
        _failure = o =>
        {
            _recording = true;
            return new LlamaServerException("exit", "out of memory", 1);
        };
        await using var llm = Create(Options(AiAcceleration.Vulkan));

        await Assert.ThrowsAsync<AiPausedException>(() => llm.GetChatClientAsync(Ct).AsTask());

        Assert.Equal([VulkanServer], _started.Select(s => s.ServerPath));
        Assert.Empty(_handles);
    }

    [Fact]
    public async Task LiveSwitch_TheNextRequestRestartsTheServerOnTheNewBuild()
    {
        var choice = AiAcceleration.Cpu;
        await using var llm = Create(() => Options(choice));
        var chat = await llm.GetChatClientAsync(Ct);
        var first = _handles.Single();

        choice = AiAcceleration.Cuda;
        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], cancellationToken: Ct);

        Assert.Equal([CpuServer, CudaServer], _started.Select(s => s.ServerPath));
        await WaitUntilAsync(() => first.Disposed);
        Assert.True(first.Stopped);
    }

    [Fact]
    public async Task RetireStaleServers_StopsAnIdleServerOfTheOldBuild_AndKeepsAMatchingOne()
    {
        var choice = AiAcceleration.Cuda;
        await using var llm = Create(() => Options(choice));
        await llm.GetChatClientAsync(Ct);
        await llm.GetEmbeddingGeneratorAsync(Ct);
        var chatServer = _handles.ElementAt(0);
        var embeddingServer = _handles.ElementAt(1);

        llm.RetireStaleServers();
        Assert.False(chatServer.Stopped);

        choice = AiAcceleration.Cpu;
        llm.RetireStaleServers();

        await WaitUntilAsync(() => chatServer.Disposed);
        Assert.True(chatServer.Stopped);
        Assert.False(embeddingServer.Stopped); // the embedding server was on the CPU build all along
        Assert.Equal(LlmStatus.Ready, llm.Status);
    }

    [Fact]
    public async Task RetireStaleServers_LeavesAServerWithARequestInFlight()
    {
        var choice = AiAcceleration.Cuda;
        await using var llm = Create(() => Options(choice));
        var chat = await llm.GetChatClientAsync(Ct);
        var server = _handles.Single();
        _requestGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = chat.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], cancellationToken: Ct);
        await WaitUntilAsync(() => _requestsSeen > 0);
        choice = AiAcceleration.Cpu;
        llm.RetireStaleServers();

        Assert.False(server.Stopped);
        _requestGate.SetResult();
        Assert.Equal("pong", (await pending).Text);
    }

    [Fact]
    public void DefaultServerPath_FollowsTheCatalogFolderNames()
    {
        var exe = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";

        Assert.Equal(Path.Combine("/m", "llama.cpp", exe), LocalLlmOptions.DefaultServerPath("/m"));
        Assert.Equal(Path.Combine("/m", "llama.cpp", exe), LocalLlmOptions.DefaultServerPath("/m", AiAcceleration.Cpu));
        Assert.Equal(Path.Combine("/m", "llama.cpp-vulkan", exe), LocalLlmOptions.DefaultServerPath("/m", AiAcceleration.Vulkan));
        Assert.Equal(Path.Combine("/m", "llama.cpp-cuda", exe), LocalLlmOptions.DefaultServerPath("/m", AiAcceleration.Cuda));
    }

    [Fact]
    public void DescribeFailure_PrefersTheLastLineOfTheServerOutput_AndStaysShort()
    {
        var withTail = new LlamaServerException("exited", "first\n\n  CUDA error: out of memory  \n", 1);
        var noTail = new LlamaServerException("Could not start 'x': The system cannot find the file.\nmore", string.Empty);
        var huge = new InvalidOperationException(new string('x', 500));

        Assert.Equal("CUDA error: out of memory", LocalLlm.DescribeFailure(withTail));
        Assert.StartsWith("Could not start 'x'", LocalLlm.DescribeFailure(noTail), StringComparison.Ordinal);
        Assert.DoesNotContain("more", LocalLlm.DescribeFailure(noTail), StringComparison.Ordinal);
        Assert.Equal(203, LocalLlm.DescribeFailure(huge).Length);
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

    private sealed class Handle(Uri baseAddress) : ILlamaServer
    {
        private int _disposed;

        public Uri BaseAddress { get; } = baseAddress;

        public bool HasExited { get; private set; }

        public bool Stopped { get; private set; }

        public bool Disposed => Volatile.Read(ref _disposed) > 0;

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            HasExited = true;
            return ValueTask.CompletedTask;
        }

        public void Kill() => HasExited = true;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposed);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The fallback against real child processes: a "GPU" llama-server that dies at load and a working CPU one.</summary>
public sealed class LocalLlmAccelerationProcessTests
{
    [Fact]
    public async Task GpuServerThatDiesAtLoad_FallsBackToTheCpuServerProcess()
    {
        Assert.SkipUnless(FakeLlamaServerScript.IsSupported, "Needs python3 on a Unix OS.");
        var ct = TestContext.Current.CancellationToken;
        using var gpu = new FakeLlamaServerScript(exitCode: 3);
        using var cpu = new FakeLlamaServerScript();
        var options = new LocalLlmOptions
        {
            LlamaServerPath = cpu.ServerPath,
            Acceleration = AiAcceleration.Cuda,
            AcceleratedServerPath = gpu.ServerPath,
            ChatModelPath = cpu.ModelPath,
            EmbeddingModelPath = cpu.EmbeddingModelPath,
            Threads = 1,
        };
        await using var llm = new LocalLlm(options, () => false);

        var chat = await llm.GetChatClientAsync(ct);
        var answer = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "привіт")], cancellationToken: ct);

        Assert.Equal("echo: привіт", answer.Text);
        Assert.Contains("-ngl", gpu.ReadArguments());
        Assert.DoesNotContain("-ngl", cpu.ReadArguments());
        var status = llm.Current;
        Assert.Equal(AccelerationFallback.StartFailed, status.Fallback);
        Assert.Equal("llama_server: exiting due to model loading error", status.Detail);
    }
}
