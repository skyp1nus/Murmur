using System.Net;
using Murmur.Intelligence.Llama;
using Murmur.Intelligence.Tests.Fakes;

namespace Murmur.Intelligence.Tests;

public sealed class LlamaServerProcessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void BuildArguments_ChatServer()
    {
        var options = new LlamaServerOptions { ServerPath = "llama-server", ModelPath = "/m/gemma.gguf", ContextSize = 4096, Threads = 3 };

        var args = LlamaServerProcess.BuildArguments(options, 5555);

        Assert.Equal(
            ["-m", "/m/gemma.gguf", "--host", "127.0.0.1", "--port", "5555", "-c", "4096", "-t", "3", "--no-webui"],
            args);
    }

    [Fact]
    public void BuildArguments_EmbeddingServerWithExtras()
    {
        var options = new LlamaServerOptions
        {
            ServerPath = "llama-server",
            ModelPath = "e.gguf",
            Embedding = true,
            ExtraArguments = ["-ub", "2048"],
        };

        var args = LlamaServerProcess.BuildArguments(options, 1);

        Assert.Equal(["--embedding", "-ub", "2048"], args.Skip(11));
        Assert.Equal("8192", args[7]);
    }

    [Fact]
    public void GetFreePort_ReturnsAnEphemeralPort()
    {
        var port = LlamaServerProcess.GetFreePort();

        Assert.InRange(port, 1024, 65535);
    }

    [Fact]
    public async Task Start_MissingBinary_ThrowsFileNotFound()
    {
        var options = new LlamaServerOptions { ServerPath = Path.Combine(Path.GetTempPath(), "no-such-llama-server"), ModelPath = "m.gguf" };

        await Assert.ThrowsAsync<FileNotFoundException>(() => LlamaServerProcess.StartAsync(options, Ct));
    }

    [Fact]
    public async Task Start_NotExecutable_ThrowsLlamaServerException()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses the Unix executable bit.");
        var path = Path.Combine(Path.GetTempPath(), "murmur-not-executable-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, "#!/bin/sh\n", Ct);
        try
        {
            var options = new LlamaServerOptions { ServerPath = path, ModelPath = "m.gguf" };

            var ex = await Assert.ThrowsAsync<LlamaServerException>(() => LlamaServerProcess.StartAsync(options, Ct));

            Assert.Contains("Could not start", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Start_WaitsForHealthThenStopsGracefully()
    {
        Assert.SkipUnless(FakeLlamaServerScript.IsSupported, "Needs python3 on a Unix OS.");
        using var script = new FakeLlamaServerScript(unhealthyPolls: 2);
        var options = new LlamaServerOptions { ServerPath = script.ServerPath, ModelPath = script.ModelPath, Threads = 2, ContextSize = 1024 };

        await using var server = await LlamaServerProcess.StartAsync(options, Ct);

        Assert.False(server.HasExited);
        Assert.Equal(new Uri($"http://127.0.0.1:{server.Port}/"), server.BaseAddress);
        using (var http = new HttpClient())
        {
            using var health = await http.GetAsync(new Uri(server.BaseAddress, "health"), Ct);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        Assert.Equal(LlamaServerProcess.BuildArguments(options, server.Port), script.ReadArguments());
        Assert.Equal(server.ProcessId, script.ReadPid());
        Assert.Contains("server is listening", server.OutputTail, StringComparison.Ordinal);

        await server.StopAsync(Ct);

        Assert.True(server.HasExited);
        Assert.True(script.WaitForProcessGone(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Start_EarlyExit_ThrowsWithStderrTail()
    {
        Assert.SkipUnless(FakeLlamaServerScript.IsSupported, "Needs python3 on a Unix OS.");
        using var script = new FakeLlamaServerScript(exitCode: 3);
        var options = new LlamaServerOptions { ServerPath = script.ServerPath, ModelPath = script.ModelPath };

        var ex = await Assert.ThrowsAsync<LlamaServerException>(() => LlamaServerProcess.StartAsync(options, Ct));

        Assert.Equal(3, ex.ExitCode);
        Assert.Contains("failed to load model 'fake.gguf'", ex.OutputTail, StringComparison.Ordinal);
        Assert.Contains("exiting due to model loading error", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_NeverHealthy_TimesOutAndKillsTheProcess()
    {
        Assert.SkipUnless(FakeLlamaServerScript.IsSupported, "Needs python3 on a Unix OS.");
        using var script = new FakeLlamaServerScript(unhealthyPolls: -1);
        var options = new LlamaServerOptions
        {
            ServerPath = script.ServerPath,
            ModelPath = script.ModelPath,
            StartupTimeout = TimeSpan.FromSeconds(1.5),
        };

        await Assert.ThrowsAsync<TimeoutException>(() => LlamaServerProcess.StartAsync(options, Ct));

        Assert.True(script.WaitForProcessGone(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Start_Cancelled_KillsTheProcess()
    {
        Assert.SkipUnless(FakeLlamaServerScript.IsSupported, "Needs python3 on a Unix OS.");
        using var script = new FakeLlamaServerScript(unhealthyPolls: -1);
        var options = new LlamaServerOptions { ServerPath = script.ServerPath, ModelPath = script.ModelPath };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LlamaServerProcess.StartAsync(options, cts.Token));

        Assert.True(script.WaitForProcessGone(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Dispose_KillsTheProcess()
    {
        Assert.SkipUnless(FakeLlamaServerScript.IsSupported, "Needs python3 on a Unix OS.");
        using var script = new FakeLlamaServerScript();
        var options = new LlamaServerOptions { ServerPath = script.ServerPath, ModelPath = script.ModelPath };
        var server = await LlamaServerProcess.StartAsync(options, Ct);

        await server.DisposeAsync();
        await server.DisposeAsync();

        Assert.True(script.WaitForProcessGone(TimeSpan.FromSeconds(5)));
    }
}

/// <summary>Runs the real llama.cpp binary (downloaded once per class) against an invalid model.</summary>
public sealed class RealLlamaServerProcessTests(RealLlamaServerFixture fixture) : IClassFixture<RealLlamaServerFixture>
{
    [Fact]
    public async Task Start_InvalidModel_ThrowsWithLlamaCppErrorTail()
    {
        Assert.SkipWhen(fixture.ServerPath is null, fixture.SkipReason ?? "llama-server unavailable.");
        var model = Path.Combine(Path.GetTempPath(), "murmur-not-a-model-" + Guid.NewGuid().ToString("N") + ".gguf");
        await File.WriteAllTextAsync(model, "this is not a gguf file", TestContext.Current.CancellationToken);
        try
        {
            var options = new LlamaServerOptions
            {
                ServerPath = fixture.ServerPath!,
                ModelPath = model,
                ContextSize = 512,
                Threads = 1,
                StartupTimeout = TimeSpan.FromSeconds(60),
            };

            var ex = await Assert.ThrowsAsync<LlamaServerException>(
                () => LlamaServerProcess.StartAsync(options, TestContext.Current.CancellationToken));

            Assert.NotEqual(0, ex.ExitCode);
            Assert.Contains("failed to load model", ex.OutputTail, StringComparison.Ordinal);
            Assert.Contains("failed to load model", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(model);
        }
    }
}
