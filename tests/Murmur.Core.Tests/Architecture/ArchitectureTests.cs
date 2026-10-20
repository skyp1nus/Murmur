namespace Murmur.Core.Tests.Architecture;

/// <summary>
/// Enforces the module boundaries from CLAUDE.md by reading the project files under <c>src/</c>.
/// </summary>
public sealed class ArchitectureTests
{
    private const string CaptureWindows = "Murmur.Capture.Windows";

    private static readonly Lazy<SourceTree> Tree = new(SourceTree.Load);

    [Fact]
    public void SourceTree_ContainsTheExpectedProjects()
    {
        string[] expected =
        [
            "Murmur.App",
            "Murmur.Capture.Windows",
            "Murmur.Core",
            "Murmur.Desktop",
            "Murmur.Intelligence",
            "Murmur.Storage",
            "Murmur.Transcription",
        ];

        Assert.Subset(Tree.Value.Projects.Keys.ToHashSet(StringComparer.Ordinal), expected.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void App_ReferencesOnlyCore()
    {
        var app = Tree.Value.Get("Murmur.App");

        var reference = Assert.Single(app.ProjectReferences);
        Assert.Equal("Murmur.Core", reference.Name);
    }

    [Fact]
    public void Core_HasNoProjectReferences()
    {
        Assert.Empty(Tree.Value.Get("Murmur.Core").ProjectReferences);
    }

    [Fact]
    public void OnlyDesktop_ReferencesCaptureWindows()
    {
        var referencing = Tree.Value.Projects.Values
            .Where(p => p.ProjectReferences.Any(r => r.Name == CaptureWindows))
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal(["Murmur.Desktop"], referencing);
    }

    [Fact]
    public void Desktop_ReferencesCaptureWindowsOnlyForTheWindowsTargetFramework()
    {
        var desktop = Tree.Value.Get("Murmur.Desktop");
        var reference = Assert.Single(desktop.ProjectReferences, r => r.Name == CaptureWindows);

        Assert.NotNull(reference.Condition);
        Assert.Contains("TargetFramework", reference.Condition, StringComparison.Ordinal);
        Assert.Contains("-windows", reference.Condition, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyCaptureAndDesktop_TargetWindows()
    {
        var offenders = Tree.Value.Projects.Values
            .Where(p => !p.Name.StartsWith("Murmur.Capture.", StringComparison.Ordinal) && p.Name != "Murmur.Desktop")
            .Where(p => p.TargetFrameworks.Any(IsWindowsTfm))
            .Select(p => $"{p.Name}: {string.Join(';', p.TargetFrameworks)}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void PlatformNeutralProjects_TargetPlainNet()
    {
        foreach (var name in new[] { "Murmur.Core", "Murmur.App", "Murmur.Storage", "Murmur.Intelligence", "Murmur.Transcription" })
        {
            var project = Tree.Value.Get(name);
            Assert.NotEmpty(project.TargetFrameworks);
            Assert.All(project.TargetFrameworks, tfm => Assert.False(IsWindowsTfm(tfm), $"{name} targets {tfm}"));
        }
    }

    [Fact]
    public void Desktop_TargetsBothPlainAndWindowsFrameworks()
    {
        var tfms = Tree.Value.Get("Murmur.Desktop").TargetFrameworks;

        Assert.Contains(tfms, tfm => !IsWindowsTfm(tfm));
        Assert.Contains(tfms, IsWindowsTfm);
    }

    [Fact]
    public void CaptureWindows_TargetsOnlyWindows()
    {
        var tfms = Tree.Value.Get(CaptureWindows).TargetFrameworks;

        Assert.NotEmpty(tfms);
        Assert.All(tfms, tfm => Assert.True(IsWindowsTfm(tfm), tfm));
    }

    [Fact]
    public void Core_WritesNoUkrainianText_OnlyCodes()
    {
        // Ukrainian below the App reaches an English UI (D-039); Core stores codes and the App words them. The one allowed
        // literal matches the text an earlier version stored.
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Tree.Value.Root, "src", "Murmur.Core"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var line in File.ReadLines(file))
            {
                var code = line.Contains("//", StringComparison.Ordinal) ? line[..line.IndexOf("//", StringComparison.Ordinal)] : line;
                if (code.Any(c => c is >= '\u0400' and <= '\u04FF') && !code.Contains("LegacyIndexingFailedPrefix", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    private static bool IsWindowsTfm(string tfm) => tfm.Contains("-windows", StringComparison.OrdinalIgnoreCase);
}
