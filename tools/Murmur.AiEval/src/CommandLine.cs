using System.Globalization;

namespace Murmur.AiEval;

public sealed record EvalOptions
{
    public required string FixturePath { get; init; }

    public required string QuestionsPath { get; init; }

    public required string OutPath { get; init; }

    /// <summary>Storage root; <c>null</c> = a fresh temporary folder that is deleted afterwards.</summary>
    public string? DataDir { get; init; }

    public string? MarkdownPath { get; init; }

    public bool RetrievalOnly { get; init; }

    public double Threshold { get; init; } = Scoring.DefaultThreshold;

    /// <summary>Hits checked per question in the retrieval modes.</summary>
    public int TopK { get; init; } = 3;

    /// <summary>Generation cap per answer; keeps CPU-only CI runs bounded.</summary>
    public int MaxOutputTokens { get; init; } = 512;

    /// <summary>CPU threads per llama-server; <c>null</c> keeps <see cref="Murmur.Intelligence.LocalLlmOptions.DefaultThreads"/>.</summary>
    public int? Threads { get; init; }
}

/// <summary>Parsed command line: an evaluation, a fixture generation, help, or an error.</summary>
public sealed record ParsedCommand(EvalOptions? Eval, string? GenerateFixtureDir, bool Help, string? Error);

public static class CommandLine
{
    public const string Usage =
        """
        Murmur.AiEval: does the AI answer lead to the right minute of the recording?

        Usage:
          Murmur.AiEval --fixture <json> --questions <json> --out <json> [options]
          Murmur.AiEval --generate-fixture <dir>

        Options:
          --data-dir <dir>         storage root (default: a temporary folder, deleted afterwards)
          --retrieval-only         no chat model: check that hybrid search top-3 contains the fact
                                   (keyword-only with each question's keywords when no embedding model)
          --markdown <md>          also write the result table to this file
          --threshold <0..1>       pass rate needed for exit code 0 (default 0.8)
          --top <n>                hits checked per question in retrieval mode (default 3)
          --max-output-tokens <n>  generation cap per answer (default 512)
          --threads <n>            CPU threads per llama-server (default: half of the logical CPUs)

        Models come from MURMUR_LLM_MODEL, MURMUR_EMBED_MODEL, MURMUR_LLAMA_SERVER (or MURMUR_MODELS_DIR).
        Exit codes: 0 pass rate >= threshold, 1 below threshold, 2 usage or setup error.
        """;

    private static readonly HashSet<string> ValueOptions =
    [
        "--fixture", "--questions", "--out", "--data-dir", "--markdown", "--generate-fixture",
        "--threshold", "--top", "--max-output-tokens", "--threads",
    ];

    public static ParsedCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? fixture = null, questions = null, output = null, dataDir = null, markdown = null, generate = null;
        var retrievalOnly = false;
        var threshold = Scoring.DefaultThreshold;
        var top = 3;
        var maxTokens = 512;
        int? threads = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    return new ParsedCommand(null, null, true, null);
                case "--retrieval-only":
                    retrievalOnly = true;
                    continue;
            }

            if (!ValueOptions.Contains(arg))
            {
                return Fail(arg.StartsWith('-') ? $"Unknown option: {arg}" : $"Unknown argument: {arg}");
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return Fail($"{arg} needs a value.");
            }

            var value = args[++i];
            switch (arg)
            {
                case "--fixture": fixture = value; break;
                case "--questions": questions = value; break;
                case "--out": output = value; break;
                case "--data-dir": dataDir = value; break;
                case "--markdown": markdown = value; break;
                case "--generate-fixture": generate = value; break;
                case "--threshold":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold) || threshold is < 0 or > 1)
                    {
                        return Fail("--threshold must be a number between 0 and 1.");
                    }

                    break;
                case "--top":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out top) || top < 1)
                    {
                        return Fail("--top must be a positive integer.");
                    }

                    break;
                case "--max-output-tokens":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out maxTokens) || maxTokens < 1)
                    {
                        return Fail("--max-output-tokens must be a positive integer.");
                    }

                    break;
                case "--threads":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
                    {
                        return Fail("--threads must be a positive integer.");
                    }

                    threads = count;
                    break;
                default:
                    return Fail($"Unknown option: {arg}");
            }
        }

        if (generate is not null)
        {
            return new ParsedCommand(null, generate, false, null);
        }

        if (fixture is null || questions is null || output is null)
        {
            return Fail("--fixture, --questions and --out are required.");
        }

        return new ParsedCommand(
            new EvalOptions
            {
                FixturePath = fixture,
                QuestionsPath = questions,
                OutPath = output,
                DataDir = dataDir,
                MarkdownPath = markdown,
                RetrievalOnly = retrievalOnly,
                Threshold = threshold,
                TopK = top,
                MaxOutputTokens = maxTokens,
                Threads = threads,
            },
            null,
            false,
            null);

        static ParsedCommand Fail(string error) => new(null, null, false, error);
    }
}
