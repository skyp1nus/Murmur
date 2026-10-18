namespace Murmur.App.Tests;

/// <summary>Locates files in the repository checkout from the test output directory.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string TokensJson => Path.Combine(Root, "docs", "design", "tokens.json");

    public static string AppDirectory => Path.Combine(Root, "src", "Murmur.App");

    public static string TokensAxaml => Path.Combine(AppDirectory, "Styles", "Tokens.axaml");

    public static string ScreenshotsDirectory => Path.Combine(Root, "docs", "screenshots");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Murmur.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Murmur.sln not found above " + AppContext.BaseDirectory);
    }
}
