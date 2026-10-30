namespace Murmur.App.Tests;

/// <summary>Locates files in the repository checkout from the test output directory.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string TokensJson => Path.Combine(Root, "docs", "design", "tokens.json");

    public static string AppDirectory => Path.Combine(Root, "src", "Murmur.App");

    public static string TokensAxaml => Path.Combine(AppDirectory, "Styles", "Tokens.axaml");

    public static string ResourcesDirectory => Path.Combine(AppDirectory, "Resources");

    /// <summary>Set to <c>1</c> to refresh the tracked screenshots in <c>docs/screenshots</c>.</summary>
    public const string UpdateScreenshotsVariable = "MURMUR_UPDATE_SCREENSHOTS";

    /// <summary>
    /// Where the screenshot tests write: <c>artifacts/screenshots</c> (untracked) by default, so a test run never dirties the tree, and the
    /// tracked <c>docs/screenshots</c> only when <see cref="UpdateScreenshotsVariable"/> is <c>1</c>.
    /// </summary>
    public static string ScreenshotsDirectory { get; } = Environment.GetEnvironmentVariable(UpdateScreenshotsVariable) == "1"
        ? Path.Combine(Root, "docs", "screenshots")
        : Path.Combine(Root, "artifacts", "screenshots");

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
