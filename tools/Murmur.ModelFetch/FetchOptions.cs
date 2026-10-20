namespace Murmur.ModelFetch;

/// <summary>Command line of the tool: <c>[--resolve-pins] [--dir &lt;path&gt; [--ids a,b] [--head-only a,b]] [--summary &lt;file&gt;]</c>.</summary>
/// <param name="Directory">Models directory; required for <c>--ids</c> and <c>--head-only</c>.</param>
/// <param name="ResolvePins">Ask the hosts for the upstream revision, SHA-256 and size of every catalog source (no download of models).</param>
internal sealed record FetchOptions(string? Directory, IReadOnlyList<string> Install, IReadOnlyList<string> HeadOnly, string? SummaryFile, bool ResolvePins = false)
{
    public const string Usage =
        "Usage: Murmur.ModelFetch [--resolve-pins] [--dir <models directory> [--ids <id,id,...>] [--head-only <id,id,...>]] [--summary <markdown file>]\n"
        + "  --resolve-pins  print the upstream commit, SHA-256 and size of every catalog source and whether the pinned values drifted (no --dir needed;\n"
        + "                  the summary goes to --summary, else to $GITHUB_STEP_SUMMARY)";

    /// <summary>Parses the arguments, or returns <c>null</c> and the reason.</summary>
    public static FetchOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        string? directory = null;
        string? summary = null;
        List<string> install = [];
        List<string> headOnly = [];
        var resolvePins = false;
        error = null;

        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (name == "--resolve-pins")
            {
                resolvePins = true;
                continue;
            }

            if (name is not ("--dir" or "--ids" or "--head-only" or "--summary"))
            {
                error = $"Unknown argument '{name}'";
                return null;
            }

            if (i + 1 >= args.Count)
            {
                error = $"Missing value for {name}";
                return null;
            }

            var value = args[++i];
            switch (name)
            {
                case "--dir":
                    directory = value;
                    break;
                case "--ids":
                    install.AddRange(Split(value));
                    break;
                case "--head-only":
                    headOnly.AddRange(Split(value));
                    break;
                default:
                    summary = value;
                    break;
            }
        }

        if (install.Count == 0 && headOnly.Count == 0 && !resolvePins)
        {
            error = string.IsNullOrWhiteSpace(directory) ? "--dir is required" : "Nothing to do: give --ids, --head-only and/or --resolve-pins";
            return null;
        }

        if ((install.Count > 0 || headOnly.Count > 0) && string.IsNullOrWhiteSpace(directory))
        {
            error = "--dir is required";
            return null;
        }

        return new FetchOptions(directory, install, headOnly, summary, resolvePins);
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
