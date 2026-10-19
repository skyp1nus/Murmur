namespace Murmur.ModelFetch;

/// <summary>Command line of the tool: <c>--dir &lt;path&gt; [--ids a,b] [--head-only a,b] [--summary &lt;file&gt;]</c>.</summary>
internal sealed record FetchOptions(string Directory, IReadOnlyList<string> Install, IReadOnlyList<string> HeadOnly, string? SummaryFile)
{
    public const string Usage = "Usage: Murmur.ModelFetch --dir <models directory> [--ids <id,id,...>] [--head-only <id,id,...>] [--summary <markdown file>]";

    /// <summary>Parses the arguments, or returns <c>null</c> and the reason.</summary>
    public static FetchOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        string? directory = null;
        string? summary = null;
        List<string> install = [];
        List<string> headOnly = [];
        error = null;

        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
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

        if (string.IsNullOrWhiteSpace(directory))
        {
            error = "--dir is required";
            return null;
        }

        if (install.Count == 0 && headOnly.Count == 0)
        {
            error = "Nothing to do: give --ids and/or --head-only";
            return null;
        }

        return new FetchOptions(directory, install, headOnly, summary);
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
