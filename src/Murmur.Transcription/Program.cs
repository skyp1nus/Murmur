using System.Text;
using Murmur.Transcription.Bench;
using Murmur.Transcription.Pipeline;
using Murmur.Transcription.Server;

namespace Murmur.Transcription;

internal static class Program
{
    /// <summary>Exit code for "invoked without a usable command".</summary>
    internal const int UsageExitCode = 2;

    internal const int FailureExitCode = 1;

    internal const string Usage =
        "Murmur.Transcription - ASR worker process for Murmur (sherpa-onnx + Parakeet TDT v3 + Silero VAD).\n" +
        "This process is started by the Murmur app; the bench and transcribe commands are for diagnostics.\n" +
        "usage:\n" +
        "  Murmur.Transcription --serve --model-dir <dir> --vad <silero_vad.onnx> [--threads N] [--idle-exit-seconds S]\n" +
        "  Murmur.Transcription --bench --model-dir <dir> --vad <file> --wav <file>[,<file>...] [--seconds 3600] [--out <json>] [--threads N]\n" +
        "  Murmur.Transcription --transcribe <16 kHz mono wav> --model-dir <dir> --vad <file> [--threads N]\n" +
        "tuning (all commands): [--parallel P] [--batch N] [--max-speech S] [--vad-threshold T] [--min-silence S]\n" +
        "  --threads N = CPU budget (default: logical cores), split into P decode workers (default N) x N/P ONNX Runtime threads;\n" +
        "  --batch N = segments per DecodeMultipleOfflineStreams call (default 1). bench extras: [--vad-only] [--save-wav <file>]";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (Array.IndexOf(args, "--serve") >= 0)
        {
            return Serve(args);
        }

        return Run(args, Console.Out, Console.Error);
    }

    /// <summary>Everything except <c>--serve</c> (which owns stdin/stdout).</summary>
    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!CliOptions.TryParse(args, out var options, out var parseError))
        {
            error.WriteLine("error: " + parseError);
            error.WriteLine(Usage);
            return UsageExitCode;
        }

        switch (options.Command)
        {
            case WorkerCommand.Help:
                output.WriteLine(Usage);
                return 0;
            case WorkerCommand.Bench:
                return Bench(options, output, error);
            case WorkerCommand.Transcribe:
                return Transcribe(options, output, error);
            case WorkerCommand.Serve:
                error.WriteLine("error: --serve must be started as the process entry point");
                return UsageExitCode;
            default:
                error.WriteLine(Usage);
                return UsageExitCode;
        }
    }

    internal static EngineOptions EngineOptionsFrom(CliOptions o)
    {
        var defaults = new PipelineOptions();
        var vad = defaults.Vad with
        {
            MaxSpeechSeconds = o.MaxSpeechSeconds ?? defaults.Vad.MaxSpeechSeconds,
            Threshold = o.VadThreshold ?? defaults.Vad.Threshold,
            MinSilenceSeconds = o.MinSilenceSeconds ?? defaults.Vad.MinSilenceSeconds,
        };
        return new EngineOptions
        {
            ModelDirectory = o.ModelDirectory!,
            VadModelPath = o.VadModelPath!,
            Threads = o.OrtThreadsPerWorker,
            Pipeline = defaults with { BatchSize = o.BatchSize, Parallelism = o.EffectiveParallelism, Vad = vad },
        };
    }

    private static int Serve(string[] args)
    {
        // stdout carries protocol JSON only: keep a private writer for it and point Console.Out at stderr, so a stray
        // Console.WriteLine anywhere in the process cannot corrupt the stream.
        var stdout = new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom, 64 * 1024) { AutoFlush = false, NewLine = "\n" };
        var stderr = Console.Error;
        Console.SetOut(stderr);
        var stdin = new StreamReader(Console.OpenStandardInput(), Utf8NoBom, detectEncodingFromByteOrderMarks: true, 64 * 1024);

        if (!CliOptions.TryParse(args, out var options, out var parseError))
        {
            stderr.WriteLine("error: " + parseError);
            stderr.WriteLine(Usage);
            return UsageExitCode;
        }

        stderr.WriteLine($"murmur-asr: serving (threads={options.Threads}: {options.EffectiveParallelism} worker(s) x {options.OrtThreadsPerWorker} ORT thread(s), batch={options.BatchSize}, idle-exit={options.IdleExitSeconds:0.#} s)");
        var engineOptions = EngineOptionsFrom(options);
        var protocol = new ProtocolWriter(stdout);

        // Last line of defence (the server's own threads catch everything): report, then exit 1 instead of crashing.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                stderr.WriteLine("murmur-asr: unhandled exception: " + e.ExceptionObject);
                protocol.Error(null, "internal error: " + (e.ExceptionObject as Exception)?.Message);
            }
            finally
            {
                Environment.Exit(FailureExitCode);
            }
        };

        var server = new WorkerServer(
            stdin,
            protocol,
            stderr,
            () => new SherpaTranscriptionEngine(engineOptions),
            TimeSpan.FromSeconds(options.IdleExitSeconds));
        var code = server.Run();
        stdout.Flush();
        return code;
    }

    private static int Bench(CliOptions options, TextWriter output, TextWriter error)
    {
        try
        {
            var result = BenchRunner.Run(options, error);
            var json = BenchRunner.ToJson(result);
            output.WriteLine(json);
            if (!string.IsNullOrEmpty(options.OutputPath))
            {
                File.WriteAllText(options.OutputPath, json + "\n", Utf8NoBom);
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            error.WriteLine("error: " + ex.Message);
            return FailureExitCode;
        }
    }

    private static int Transcribe(CliOptions options, TextWriter output, TextWriter error)
    {
        try
        {
            using var engine = new SherpaTranscriptionEngine(EngineOptionsFrom(options));
            var text = new StringBuilder();
            var stats = engine.Transcribe(
                options.TranscribePath!,
                s =>
                {
                    output.WriteLine($"[{s.Start,8:0.000} - {s.End,8:0.000}] {s.Text}");
                    text.Append(text.Length > 0 ? " " : string.Empty).Append(s.Text);
                },
                null,
                CancellationToken.None);
            output.WriteLine("Text: " + text);
            error.WriteLine($"audio {stats.AudioSeconds:0.000} s, {stats.Segments} segments, {stats.Words} words, model load {engine.LoadMilliseconds:0} ms");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            error.WriteLine("error: " + ex.Message);
            return FailureExitCode;
        }
    }
}
