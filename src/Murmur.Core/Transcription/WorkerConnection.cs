using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Murmur.Core.Transcription.Protocol;

namespace Murmur.Core.Transcription;

/// <summary>
/// One running ASR worker process: JSON-lines requests to its stdin, parsed events from its stdout (a pump task
/// completes <see cref="Events"/> when stdout closes), and a bounded tail of its stderr for error reports.
/// </summary>
internal sealed class WorkerConnection : IAsyncDisposable
{
    private const int StderrTailLines = 40;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process _process;
    private readonly Channel<WorkerEvent> _events = Channel.CreateUnbounded<WorkerEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Queue<string> _stderr = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _pump;
    private int _disposed;

    private WorkerConnection(Process process)
    {
        _process = process;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                AppendStderr(line);
            }
        };
        _process.BeginErrorReadLine();
        _pump = Task.Run(PumpStdoutAsync);
    }

    public ChannelReader<WorkerEvent> Events => _events.Reader;

    /// <summary>The worker closed its stdout or exited.</summary>
    public bool HasExited
    {
        get
        {
            if (_pump.IsCompleted)
            {
                return true;
            }

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

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public string StderrTail
    {
        get
        {
            lock (_stderr)
            {
                return string.Join('\n', _stderr);
            }
        }
    }

    /// <exception cref="AsrWorkerException">The executable could not be started.</exception>
    public static WorkerConnection Start(string fileName, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new AsrWorkerException($"Could not start the ASR worker '{fileName}': {ex.Message}", ex);
        }

        if (process is null)
        {
            throw new AsrWorkerException($"Could not start the ASR worker '{fileName}'.");
        }

        process.StandardInput.NewLine = "\n";
        return new WorkerConnection(process);
    }

    /// <exception cref="IOException">The worker's stdin is closed (it exited).</exception>
    public async ValueTask SendAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(request, WorkerJsonContext.Default.WorkerRequest);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stdin = _process.StandardInput;
            await stdin.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The ASR worker's stdin is closed.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>The next event, or <c>null</c> once the worker closed its stdout.</summary>
    public async ValueTask<WorkerEvent?> ReadAsync(CancellationToken cancellationToken)
    {
        var reader = _events.Reader;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.TryRead(out var e))
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>Waits until the process exited and its output was drained.</summary>
    public async ValueTask<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await _pump.WaitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Asks the worker to exit (shutdown request + stdin EOF); kills it after <paramref name="timeout"/>.</summary>
    public async ValueTask ShutdownAsync(TimeSpan timeout)
    {
        if (!HasExited)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                await SendAsync(new WorkerRequest { Kind = WorkerKinds.Shutdown }, cts.Token).ConfigureAwait(false);
                _process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
            {
                // Already gone or not listening; the kill below takes care of it.
            }

            if (!await WaitForExitAsync(timeout).ConfigureAwait(false))
            {
                Kill();
            }
        }

        await DisposeAsync().ConfigureAwait(false);
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Exited in the meantime.
        }
    }

    /// <summary>Kills the worker if it is still running and releases the process handle.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Kill();
        await WaitForExitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _process.Dispose();
        _writeLock.Dispose();
    }

    private async Task PumpStdoutAsync()
    {
        try
        {
            var stdout = _process.StandardOutput;
            string? line;
            while ((line = await stdout.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                WorkerEvent? e = null;
                try
                {
                    e = JsonSerializer.Deserialize(line, WorkerJsonContext.Default.WorkerEvent);
                }
                catch (JsonException)
                {
                    // Not a protocol line; keep it for the error report.
                }

                if (e is null || string.IsNullOrEmpty(e.Kind))
                {
                    AppendStderr("stdout: " + line);
                    continue;
                }

                _events.Writer.TryWrite(e);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            AppendStderr("stdout failed: " + ex.Message);
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    private void AppendStderr(string line)
    {
        lock (_stderr)
        {
            _stderr.Enqueue(line);
            while (_stderr.Count > StderrTailLines)
            {
                _stderr.Dequeue();
            }
        }
    }
}
