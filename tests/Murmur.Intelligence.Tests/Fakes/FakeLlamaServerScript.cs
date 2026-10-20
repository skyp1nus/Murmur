using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Murmur.Intelligence.Tests.Fakes;

/// <summary>
/// Writes a python3 stand-in for <c>llama-server</c> into a temp folder: it records its argv and pid, answers
/// <c>/health</c> (503 for the first <c>unhealthyPolls</c> requests, then 200), echoes chat completions and returns
/// small embeddings. Unix only (shebang + executable bit).
/// </summary>
internal sealed class FakeLlamaServerScript : IDisposable
{
    private const string Template = """
        #!/usr/bin/env python3
        import http.server, json, os, sys
        here = os.path.dirname(os.path.abspath(__file__))
        args = sys.argv[1:]
        with open(os.path.join(here, "args.json"), "w") as f:
            json.dump(args, f)
        with open(os.path.join(here, "pid"), "w") as f:
            f.write(str(os.getpid()))
        UNHEALTHY = __UNHEALTHY__
        EXIT_CODE = __EXIT__
        STARTUP_LINE = "__STARTUP__"
        if STARTUP_LINE:
            sys.stderr.write(STARTUP_LINE + "\n")
            sys.stderr.flush()
        if EXIT_CODE >= 0:
            sys.stderr.write("load_model: failed to load model 'fake.gguf'\n")
            sys.stderr.write("llama_server: exiting due to model loading error\n")
            sys.stderr.flush()
            sys.exit(EXIT_CODE)
        port = int(args[args.index("--port") + 1])
        polls = [0]

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def reply(self, status, obj):
                body = json.dumps(obj).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def do_GET(self):
                if self.path == "/health":
                    polls[0] += 1
                    if UNHEALTHY >= 0 and polls[0] > UNHEALTHY:
                        self.reply(200, {"status": "ok"})
                    else:
                        self.reply(503, {"error": {"code": 503, "message": "Loading model"}})
                else:
                    self.reply(404, {"error": {"code": 404, "message": "not found"}})

            def do_POST(self):
                length = int(self.headers.get("Content-Length", "0"))
                req = json.loads(self.rfile.read(length) or b"{}")
                if self.path == "/v1/chat/completions":
                    text = "echo: " + req["messages"][-1]["content"]
                    self.reply(200, {"id": "x", "choices": [{"index": 0, "message": {"role": "assistant", "content": text}, "finish_reason": "stop"}]})
                elif self.path == "/v1/embeddings":
                    data = [{"index": i, "embedding": [3.0, 4.0]} for i in range(len(req["input"]))]
                    self.reply(200, {"data": data})
                else:
                    self.reply(404, {"error": {"code": 404, "message": "not found"}})

        sys.stderr.write("main: server is listening on http://127.0.0.1:%d\n" % port)
        sys.stderr.flush()
        http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
        """;

    /// <param name="startupLine">A line the server prints to stderr before it listens (e.g. llama.cpp's "no usable GPU found").</param>
    public FakeLlamaServerScript(int unhealthyPolls = 0, int exitCode = -1, string startupLine = "")
    {
        Directory = Path.Combine(Path.GetTempPath(), "murmur-fake-llama-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        ServerPath = Path.Combine(Directory, "llama-server");
        var script = Template
            .Replace("__UNHEALTHY__", unhealthyPolls.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__EXIT__", exitCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__STARTUP__", startupLine, StringComparison.Ordinal);
        File.WriteAllText(ServerPath, script.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(ServerPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        ModelPath = Path.Combine(Directory, "model.gguf");
        File.WriteAllText(ModelPath, "GGUF fake");
        EmbeddingModelPath = Path.Combine(Directory, "embedding.gguf");
        File.WriteAllText(EmbeddingModelPath, "GGUF fake");
    }

    public string Directory { get; }

    public string ServerPath { get; }

    public string ModelPath { get; }

    public string EmbeddingModelPath { get; }

    /// <summary>python3 on PATH and a Unix shebang-capable OS.</summary>
    public static bool IsSupported { get; } = !OperatingSystem.IsWindows() && HasPython();

    public string[] ReadArguments() =>
        JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(Directory, "args.json"))) ?? [];

    public int ReadPid() => int.Parse(File.ReadAllText(Path.Combine(Directory, "pid")), CultureInfo.InvariantCulture);

    /// <summary>Waits up to <paramref name="timeout"/> for the recorded process to be gone.</summary>
    public bool WaitForProcessGone(TimeSpan timeout)
    {
        var pid = ReadPid();
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            if (!IsAlive(pid))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return !IsAlive(pid);
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            // A zombie still has an entry; /proc/<pid>/stat shows state 'Z'.
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var state = stat[(stat.LastIndexOf(')') + 2)..][0];
            return state != 'Z' && state != 'X';
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static bool HasPython()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("python3", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
