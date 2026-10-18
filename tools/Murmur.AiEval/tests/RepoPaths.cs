namespace Murmur.AiEval.Tests;

internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Fixtures => Path.Combine(Root, "tests", "fixtures");

    public static string FixtureFile => Path.Combine(Fixtures, FixtureGenerator.FixtureFileName);

    public static string QuestionsFile => Path.Combine(Fixtures, FixtureGenerator.QuestionsFileName);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Murmur.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Murmur.sln not found above " + AppContext.BaseDirectory);
    }
}
