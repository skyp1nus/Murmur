using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Murmur.Core.Transcription.Protocol;

namespace Murmur.Transcription.Tests;

/// <summary>
/// Runs the real worker (<c>dotnet Murmur.Transcription.dll</c>, copied next to the test assembly by the project
/// reference) and talks the JSON-lines protocol to it. Every stdout line must be a protocol event.
/// </summary>
internal sealed class WorkerProcess : IDisposable
{
    private readonly Process _process;
    private readonly BlockingCollection<string> _lines = [];
    private readonly StringBuilder _stderr = new();
    private readonly Task _stdoutPump;

    private WorkerProcess(Process process)
    {
        _process = process;
        _stdoutPump = Task.Factory.StartNew(() =>
        {
            string? line;
            while ((line = _process.StandardOutput.ReadLine()) is not null)
            {
                _lines.Add(line);
            }

            _lines.CompleteAdding();
        }, TaskCreationOptions.LongRunning);
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (_stderr)
                {
                    _stderr.AppendLine(e.Data);
                }
            }
        };
        _process.BeginErrorReadLine();
    }

    public string Stderr
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    public List<string> RawLines { get; } = [];

    public static string WorkerDll => Path.Combine(AppContext.BaseDirectory, "Murmur.Transcription.dll");

    public static WorkerProcess Start(params string[] args)
    {
        Assert.True(File.Exists(WorkerDll), "worker not found next to the tests: " + WorkerDll);
        var psi = new ProcessStartInfo(DotnetHost())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(WorkerDll);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start the worker");
        process.StandardInput.NewLine = "\n";
        return new WorkerProcess(process);
    }

    /// <summary>Server-mode command line with test models (missing models are fine for tests that never load them).</summary>
    public static WorkerProcess Serve(params string[] extra) =>
        Start(["--serve", "--model-dir", Models.ParakeetDir, "--vad", Models.Vad, .. extra]);

    public void SendRaw(string line)
    {
        _process.StandardInput.WriteLine(line);
        _process.StandardInput.Flush();
    }

    public void Send(WorkerRequest request) => SendRaw(JsonSerializer.Serialize(request, WorkerJsonContext.Default.WorkerRequest));

    public void CloseStdin() => _process.StandardInput.Close();

    public WorkerEvent Next(TimeSpan timeout)
    {
        if (!_lines.TryTake(out var line, timeout))
        {
            Assert.Fail($"no event within {timeout.TotalSeconds:0} s (exited: {_process.HasExited}). stderr:\n{Stderr}");
        }

        return Parse(line);
    }

    private WorkerEvent Parse(string line)
    {
        RawLines.Add(line);
        try
        {
            var e = JsonSerializer.Deserialize(line, WorkerJsonContext.Default.WorkerEvent);
            Assert.NotNull(e);
            return e;
        }
        catch (JsonException ex)
        {
            Assert.Fail($"stdout line is not a protocol event: '{line}' ({ex.Message})");
            throw;
        }
    }

    /// <summary>Events until the terminal event of <paramref name="requestId"/>.</summary>
    public List<WorkerEvent> UntilTerminal(string requestId, TimeSpan timeout)
    {
        var events = new List<WorkerEvent>();
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var left = timeout - sw.Elapsed;
            var e = Next(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            events.Add(e);
            if (e.RequestId == requestId && e.Kind is WorkerKinds.Done or WorkerKinds.Error)
            {
                return events;
            }
        }
    }

    public int WaitForExit(TimeSpan timeout)
    {
        Assert.True(_process.WaitForExit(timeout), $"worker did not exit within {timeout.TotalSeconds:0} s. stderr:\n{Stderr}");
        _process.WaitForExit(); // drains the async stderr reader
        _stdoutPump.Wait(timeout);
        foreach (var line in _lines.GetConsumingEnumerable())
        {
            Parse(line); // trailing output must be protocol JSON as well
        }

        return _process.ExitCode;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }

    /// <summary>The dotnet host running these tests (works without dotnet on PATH or DOTNET_ROOT).</summary>
    private static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host))
        {
            return host;
        }

        // <root>/shared/Microsoft.NETCore.App/<version>/
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Directory.GetParent(runtimeDir)?.Parent?.Parent?.FullName;
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (root is not null && File.Exists(Path.Combine(root, exe)))
        {
            return Path.Combine(root, exe);
        }

        return exe;
    }
}
