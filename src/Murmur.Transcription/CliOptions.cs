using System.Globalization;

namespace Murmur.Transcription;

internal enum WorkerCommand
{
    None,
    Help,
    Serve,
    Bench,
    Transcribe,
}

/// <summary>Command line of the worker. Hand-written parser: no reflection, no extra dependency.</summary>
internal sealed record CliOptions
{
    public const int DefaultIdleExitSeconds = 120;
    public const int DefaultBatchSize = 1;
    public const int DefaultBenchSeconds = 3600;

    public WorkerCommand Command { get; init; }

    public string? ModelDirectory { get; init; }

    public string? VadModelPath { get; init; }

    /// <summary>Total CPU threads for ASR (decode workers x ONNX Runtime threads per call).</summary>
    public int Threads { get; init; } = Environment.ProcessorCount;

    public int BatchSize { get; init; } = DefaultBatchSize;

    /// <summary>Concurrent decode workers; <c>null</c> = one per thread (measured fastest, see PipelineOptions).</summary>
    public int? Parallelism { get; init; }

    public int EffectiveParallelism => Math.Max(1, Math.Min(Parallelism ?? Threads, Threads));

    /// <summary>ONNX Runtime intra-op threads per decode call.</summary>
    public int OrtThreadsPerWorker => Math.Max(1, Threads / EffectiveParallelism);

    public double IdleExitSeconds { get; init; } = DefaultIdleExitSeconds;

    public IReadOnlyList<string> BenchWavs { get; init; } = [];

    public double BenchSeconds { get; init; } = DefaultBenchSeconds;

    public string? OutputPath { get; init; }

    public string? TranscribePath { get; init; }

    /// <summary>Bench only: write the synthetic signal to this WAV and transcribe it from the file (server path).</summary>
    public string? SaveWavPath { get; init; }

    /// <summary>Bench only: time reading + VAD without loading the ASR model.</summary>
    public bool VadOnly { get; init; }

    public float? MaxSpeechSeconds { get; init; }

    public float? VadThreshold { get; init; }

    public float? MinSilenceSeconds { get; init; }

    public static bool TryParse(IReadOnlyList<string> args, out CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        var o = new CliOptions();
        error = null;
        var commands = 0;

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string Value()
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new FormatException($"{a} needs a value");
                }

                return args[++i];
            }

            try
            {
                switch (a)
                {
                    case "--help" or "-h" or "-?":
                        o = o with { Command = WorkerCommand.Help };
                        commands++;
                        break;
                    case "--serve":
                        o = o with { Command = WorkerCommand.Serve };
                        commands++;
                        break;
                    case "--bench":
                        o = o with { Command = WorkerCommand.Bench };
                        commands++;
                        break;
                    case "--transcribe":
                        o = o with { Command = WorkerCommand.Transcribe, TranscribePath = Value() };
                        commands++;
                        break;
                    case "--model-dir":
                        o = o with { ModelDirectory = Value() };
                        break;
                    case "--vad":
                        o = o with { VadModelPath = Value() };
                        break;
                    case "--threads":
                        o = o with { Threads = PositiveInt(a, Value()) };
                        break;
                    case "--batch":
                        o = o with { BatchSize = PositiveInt(a, Value()) };
                        break;
                    case "--parallel":
                        o = o with { Parallelism = PositiveInt(a, Value()) };
                        break;
                    case "--idle-exit-seconds":
                        o = o with { IdleExitSeconds = NonNegative(a, Value()) };
                        break;
                    case "--wav":
                        o = o with { BenchWavs = Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
                        break;
                    case "--seconds":
                        o = o with { BenchSeconds = Positive(a, Value()) };
                        break;
                    case "--out":
                        o = o with { OutputPath = Value() };
                        break;
                    case "--vad-only":
                        o = o with { VadOnly = true };
                        break;
                    case "--save-wav":
                        o = o with { SaveWavPath = Value() };
                        break;
                    case "--max-speech":
                        o = o with { MaxSpeechSeconds = (float)Positive(a, Value()) };
                        break;
                    case "--vad-threshold":
                        o = o with { VadThreshold = (float)Positive(a, Value()) };
                        break;
                    case "--min-silence":
                        o = o with { MinSilenceSeconds = (float)Positive(a, Value()) };
                        break;
                    default:
                        throw new FormatException($"unknown argument '{a}'");
                }
            }
            catch (FormatException ex)
            {
                options = o;
                error = ex.Message;
                return false;
            }
        }

        options = o;
        if (commands > 1)
        {
            error = "use exactly one of --serve, --bench, --transcribe";
            return false;
        }

        if (o.Command is WorkerCommand.Serve or WorkerCommand.Bench or WorkerCommand.Transcribe)
        {
            if (string.IsNullOrEmpty(o.ModelDirectory) || string.IsNullOrEmpty(o.VadModelPath))
            {
                error = "--model-dir and --vad are required";
                return false;
            }
        }

        if (o.Command == WorkerCommand.Bench && o.BenchWavs.Count == 0)
        {
            error = "--bench needs --wav <file>[,<file>...]";
            return false;
        }

        return true;
    }

    private static int PositiveInt(string name, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : throw new FormatException($"{name} must be a positive integer, got '{value}'");

    private static double Positive(string name, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 && double.IsFinite(v)
            ? v
            : throw new FormatException($"{name} must be a positive number, got '{value}'");

    private static double NonNegative(string name, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && double.IsFinite(v)
            ? v
            : throw new FormatException($"{name} must be a non-negative number, got '{value}'");
}
