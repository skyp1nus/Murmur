using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Murmur.Intelligence.Llama;

/// <summary>How to start one <c>llama-server</c> child process.</summary>
public sealed record LlamaServerOptions
{
    /// <summary>Path of the <c>llama-server</c> executable.</summary>
    public required string ServerPath { get; init; }

    /// <summary>GGUF model file.</summary>
    public required string ModelPath { get; init; }

    /// <summary><c>-c</c>: context size in tokens.</summary>
    public int ContextSize { get; init; } = 8192;

    /// <summary><c>-t</c>: CPU threads.</summary>
    public int Threads { get; init; } = LocalLlmOptions.DefaultThreads;

    /// <summary>Adds <c>--embedding</c>: the server only serves <c>/v1/embeddings</c>.</summary>
    public bool Embedding { get; init; }

    /// <summary>Extra arguments appended verbatim after the standard ones.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>How long to wait for <c>GET /health</c> to return 200.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>How long <see cref="LlamaServerProcess.StopAsync"/> waits for a graceful exit before killing.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Port to listen on; 0 picks a free localhost port.</summary>
    public int Port { get; init; }
}

/// <summary>A running OpenAI-compatible model server.</summary>
internal interface ILlamaServer : IAsyncDisposable
{
    /// <summary>Base address such as <c>http://127.0.0.1:51234/</c>.</summary>
    Uri BaseAddress { get; }

    bool HasExited { get; }

    /// <summary>Graceful shutdown, then a kill if the process does not exit in time.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken);

    /// <summary>Kills the process tree immediately.</summary>
    void Kill();
}

/// <summary>Raised when <c>llama-server</c> fails to start; <see cref="OutputTail"/> holds its last stderr lines.</summary>
public sealed class LlamaServerException : Exception
{
    public LlamaServerException(string message, string outputTail, int? exitCode = null, Exception? innerException = null)
        : base(BuildMessage(message, outputTail), innerException)
    {
        OutputTail = outputTail;
        ExitCode = exitCode;
    }

    /// <summary>Last lines the server wrote to stderr.</summary>
    public string OutputTail { get; }

    /// <summary>Exit code, if the process exited.</summary>
    public int? ExitCode { get; }

    private static string BuildMessage(string message, string tail) =>
        tail.Length == 0 ? message : message + Environment.NewLine + "llama-server output:" + Environment.NewLine + tail;
}

/// <summary>
/// Supervises one <c>llama-server</c> child bound to 127.0.0.1. It is started with <see cref="StartAsync"/>, is ready once
/// <c>/health</c> returns 200, and is stopped gracefully (SIGTERM on Unix) with a kill as fallback. On Windows the
/// child is put in a kill-on-close Job object so it never outlives the app.
/// </summary>
public sealed class LlamaServerProcess : ILlamaServer
{
    private const int TailLines = 40;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly Process _process;
    private readonly LlamaServerOptions _options;
    private readonly Queue<string> _tail = new();
    private readonly IDisposable? _job;
    private int _disposed;

    private LlamaServerProcess(Process process, LlamaServerOptions options, int port, IDisposable? job)
    {
        _process = process;
        _options = options;
        _job = job;
        Port = port;
        BaseAddress = new Uri($"http://127.0.0.1:{port}/");
    }

    public int Port { get; }

    public Uri BaseAddress { get; }

    public int ProcessId => _process.Id;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>Last lines written to stderr (llama.cpp logs there).</summary>
    public string OutputTail
    {
        get
        {
            lock (_tail)
            {
                return string.Join(Environment.NewLine, _tail);
            }
        }
    }

    /// <summary>Command-line arguments for <paramref name="options"/> listening on <paramref name="port"/>.</summary>
    public static IReadOnlyList<string> BuildArguments(LlamaServerOptions options, int port)
    {
        ArgumentNullException.ThrowIfNull(options);
        var args = new List<string>
        {
            "-m", options.ModelPath,
            "--host", "127.0.0.1",
            "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c", options.ContextSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-t", options.Threads.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--no-webui",
        };
        if (options.Embedding)
        {
            args.Add("--embedding");
        }

        args.AddRange(options.ExtraArguments);
        return args;
    }

    /// <summary>Picks a free TCP port on 127.0.0.1.</summary>
    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Starts the server and waits until it is healthy.</summary>
    /// <exception cref="LlamaServerException">The process could not start or exited early.</exception>
    /// <exception cref="TimeoutException">Not healthy within <see cref="LlamaServerOptions.StartupTimeout"/>.</exception>
    public static async Task<LlamaServerProcess> StartAsync(LlamaServerOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(options.ServerPath))
        {
            throw new FileNotFoundException("llama-server executable not found.", options.ServerPath);
        }

        var port = options.Port > 0 ? options.Port : GetFreePort();
        var startInfo = new ProcessStartInfo(options.ServerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = false,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ServerPath)) ?? Environment.CurrentDirectory,
        };
        foreach (var arg in BuildArguments(options, port))
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        bool started;
        try
        {
            started = process.Start();
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            throw new LlamaServerException($"Could not start '{options.ServerPath}': {ex.Message}", string.Empty, innerException: ex);
        }

        if (!started)
        {
            process.Dispose();
            throw new LlamaServerException($"Could not start '{options.ServerPath}'.", string.Empty);
        }

        IDisposable? job = null;
        if (OperatingSystem.IsWindows())
        {
            job = KillOnCloseJob.TryAssign(process.Handle);
        }

        var server = new LlamaServerProcess(process, options, port, job);
        process.ErrorDataReceived += server.OnOutput;
        process.OutputDataReceived += server.OnOutput;
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            await server.WaitUntilHealthyAsync(cancellationToken).ConfigureAwait(false);
            return server;
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
        {
            return;
        }

        lock (_tail)
        {
            _tail.Enqueue(e.Data);
            while (_tail.Count > TailLines)
            {
                _tail.Dequeue();
            }
        }
    }

    private async Task WaitUntilHealthyAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasExited)
            {
                // Let the async readers drain the pipes so the tail contains the final error lines.
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                throw new LlamaServerException(
                    $"llama-server exited with code {_process.ExitCode} before it became healthy.", OutputTail, _process.ExitCode);
            }

            try
            {
                using var response = await http.GetAsync("health", cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Per-request timeout while the model loads.
            }

            if (Stopwatch.GetElapsedTime(started) > _options.StartupTimeout)
            {
                throw new TimeoutException(
                    $"llama-server did not become healthy within {_options.StartupTimeout.TotalSeconds:0} s.{Environment.NewLine}{OutputTail}");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (HasExited)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            // Graceful: llama-server handles SIGTERM by shutting the HTTP server down and freeing the model.
            _ = NativeMethods.Kill(_process.Id, NativeMethods.SigTerm);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.StopTimeout);
            try
            {
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                // Fall through to the kill.
            }
        }

        // Windows has no console-less graceful signal for a child; the model holds no state worth flushing.
        Kill();
        try
        {
            using var timeout = new CancellationTokenSource(_options.StopTimeout);
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The OS reaps it; nothing more we can do.
        }
    }

    /// <inheritdoc />
    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Exiting concurrently or access denied; nothing else to do.
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Kill();
        try
        {
            _process.WaitForExit(TimeSpan.FromSeconds(2));
        }
        catch (InvalidOperationException)
        {
            // Never started.
        }

        _process.ErrorDataReceived -= OnOutput;
        _process.OutputDataReceived -= OnOutput;
        _process.Dispose();
        _job?.Dispose();
        return ValueTask.CompletedTask;
    }
}
