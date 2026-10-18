using System.Globalization;
using System.Text;
using System.Text.Json;
using Murmur.Core.Audio;
using Murmur.Core.Transcription.Protocol;

namespace Murmur.Core.FakeAsrWorker;

/// <summary>
/// Fake ASR worker for the WorkerAsrClient tests. Started like the real one
/// (<c>--serve --model-dir D --vad V --threads N --idle-exit-seconds S</c>); its behaviour comes from
/// <c>D/fake-worker.ini</c> (<c>key=value</c> lines) so parallel tests never share settings:
/// <list type="bullet">
/// <item><c>mode</c>: <c>normal</c> (default), <c>error</c> (error event per job), <c>crash</c> (exit 3 after the first
/// segment), <c>crash-on-start</c> (exit 4 before ready), <c>hang</c> (one segment, then ignores everything),
/// <c>fatal</c> (error without request id, exit 1), <c>no-ready</c> (never says ready).</item>
/// <item><c>segments</c> per job (default 3), spread evenly over the WAV duration; <c>delayMs</c> before each event.</item>
/// </list>
/// It exits with code 0 after <c>--idle-exit-seconds</c> without a job, like the real worker.
/// Every start and request is appended to <c>D/fake-worker.log</c>.
/// </summary>
internal static class Program
{
    private static readonly object OutputLock = new();
    private static readonly object JobLock = new();
    private static string _logPath = string.Empty;
    private static long _lastActivity = Environment.TickCount64;
    private static int _running;

    public static int Main(string[] args)
    {
        var modelDir = Arg(args, "--model-dir") ?? Environment.CurrentDirectory;
        _logPath = Path.Combine(modelDir, "fake-worker.log");
        var settings = ReadSettings(Path.Combine(modelDir, "fake-worker.ini"));
        var mode = settings.GetValueOrDefault("mode", "normal");
        var segments = int.Parse(settings.GetValueOrDefault("segments", "3"), CultureInfo.InvariantCulture);
        var delay = int.Parse(settings.GetValueOrDefault("delayMs", "0"), CultureInfo.InvariantCulture);

        Log("start " + string.Join(' ', args));
        Console.OutputEncoding = new UTF8Encoding(false);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        if (mode == "crash-on-start")
        {
            Console.Error.WriteLine("fake-worker: cannot load the model (simulated)");
            return 4;
        }

        if (mode != "no-ready")
        {
            Write(stdout, new WorkerEvent { Kind = WorkerKinds.Ready });
        }

        // Like the real worker: exit 0 after --idle-exit-seconds without a job.
        var idleSeconds = int.Parse(Arg(args, "--idle-exit-seconds") ?? "0", CultureInfo.InvariantCulture);
        if (idleSeconds > 0)
        {
            new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(50);
                    if (Volatile.Read(ref _running) == 0 && Environment.TickCount64 - Volatile.Read(ref _lastActivity) > idleSeconds * 1000L)
                    {
                        Log("idle exit");
                        Environment.Exit(0);
                    }
                }
            })
            { IsBackground = true }.Start();
        }

        string? cancelled = null;
        Thread? job = null;
        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            Log("recv " + line);
            Volatile.Write(ref _lastActivity, Environment.TickCount64);
            var request = JsonSerializer.Deserialize(line, WorkerJsonContext.Default.WorkerRequest)!;
            if (mode == "hang" && job is not null)
            {
                continue;
            }

            switch (request.Kind)
            {
                case WorkerKinds.Shutdown:
                    Log("shutdown");
                    return 0;
                case WorkerKinds.Ping:
                    Write(stdout, new WorkerEvent { Kind = WorkerKinds.Pong, RequestId = request.RequestId });
                    break;
                case WorkerKinds.Cancel:
                    lock (JobLock)
                    {
                        cancelled = request.RequestId;
                    }

                    break;
                case WorkerKinds.Transcribe:
                    job?.Join();
                    var id = request.RequestId!;
                    var audio = request.AudioPath!;
                    Volatile.Write(ref _running, 1);
                    job = new Thread(() =>
                    {
                        var exitCode = RunJob(stdout, mode, id, audio, segments, delay, () =>
                        {
                            lock (JobLock)
                            {
                                return cancelled == id;
                            }
                        });
                        Volatile.Write(ref _lastActivity, Environment.TickCount64);
                        Volatile.Write(ref _running, 0);
                        if (exitCode is { } code)
                        {
                            Console.Error.Flush();
                            Environment.Exit(code);
                        }
                    })
                    { IsBackground = true };
                    job.Start();
                    break;
            }
        }

        Log("stdin closed");
        job?.Join(TimeSpan.FromSeconds(5));
        return 0;
    }

    /// <returns>An exit code when the process must die now, else <c>null</c>.</returns>
    private static int? RunJob(StreamWriter stdout, string mode, string id, string audio, int segments, int delay, Func<bool> isCancelled)
    {
        if (mode == "error")
        {
            Write(stdout, new WorkerEvent { Kind = WorkerKinds.Error, RequestId = id, Error = "simulated failure" });
            return null;
        }

        if (mode == "fatal")
        {
            Console.Error.WriteLine("fake-worker: fatal: boom");
            Write(stdout, new WorkerEvent { Kind = WorkerKinds.Error, Error = "internal error: boom" });
            return 1;
        }

        double duration;
        try
        {
            using var reader = WavReader.Open(audio);
            duration = reader.Duration.TotalSeconds;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Write(stdout, new WorkerEvent { Kind = WorkerKinds.Error, RequestId = id, Error = "cannot read audio: " + ex.Message });
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(audio);
        var step = duration / Math.Max(1, segments);
        for (var k = 0; k < segments; k++)
        {
            if (delay > 0)
            {
                Thread.Sleep(delay);
            }

            if (isCancelled())
            {
                Write(stdout, new WorkerEvent { Kind = WorkerKinds.Error, RequestId = id, Error = "cancelled" });
                return null;
            }

            var start = k * step;
            var end = (k + 1) * step;
            var mid = (start + end) / 2;
            var text = $"{stem} {k.ToString(CultureInfo.InvariantCulture)}";
            Write(stdout, new WorkerEvent
            {
                Kind = WorkerKinds.Segment,
                RequestId = id,
                Segment = new WorkerSegment(start, end, text, [new WorkerWord(stem, start, mid), new WorkerWord(k.ToString(CultureInfo.InvariantCulture), mid, end)]),
            });
            Write(stdout, new WorkerEvent { Kind = WorkerKinds.Progress, RequestId = id, ProcessedSeconds = end, TotalSeconds = duration });

            if (mode == "crash")
            {
                Console.Error.WriteLine("fake-worker: simulated crash in the decoder");
                return 3;
            }

            if (mode == "hang")
            {
                Log("hanging");
                Thread.Sleep(Timeout.Infinite);
            }
        }

        Write(stdout, new WorkerEvent { Kind = WorkerKinds.Done, RequestId = id, ProcessedSeconds = duration, TotalSeconds = duration });
        return null;
    }

    private static void Write(StreamWriter stdout, WorkerEvent e)
    {
        var json = JsonSerializer.Serialize(e, WorkerJsonContext.Default.WorkerEvent);
        lock (OutputLock)
        {
            stdout.WriteLine(json);
        }
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static Dictionary<string, string> ReadSettings(string path)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return settings;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var eq = raw.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                settings[raw[..eq].Trim()] = raw[(eq + 1)..].Trim();
            }
        }

        return settings;
    }

    private static void Log(string message)
    {
        lock (OutputLock)
        {
            File.AppendAllText(_logPath, message + "\n");
        }
    }
}
