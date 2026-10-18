using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Murmur.Core.Tests.Architecture;

/// <summary>A project reference as written in a .csproj, resolved to the referenced project name.</summary>
internal sealed record ProjectReferenceInfo(string Name, string Include, string? Condition);

/// <summary>The parts of a .csproj the architecture rules look at, with MSBuild properties expanded.</summary>
internal sealed record ProjectInfo(string Name, string Path, IReadOnlyList<string> TargetFrameworks, IReadOnlyList<ProjectReferenceInfo> ProjectReferences);

/// <summary>Parses every <c>src/**/*.csproj</c> of the repository with <see cref="XDocument"/>.</summary>
internal sealed partial class SourceTree
{
    private SourceTree(string root, IReadOnlyDictionary<string, ProjectInfo> projects)
    {
        Root = root;
        Projects = projects;
    }

    public string Root { get; }

    public IReadOnlyDictionary<string, ProjectInfo> Projects { get; }

    public ProjectInfo Get(string name) =>
        Projects.TryGetValue(name, out var project)
            ? project
            : throw new InvalidOperationException($"Project {name} not found under {System.IO.Path.Combine(Root, "src")}.");

    public static SourceTree Load()
    {
        var root = FindRepositoryRoot();
        var propsPath = System.IO.Path.Combine(root, "Directory.Build.props");
        var globals = ReadUnconditionalProperties(XDocument.Load(propsPath), propsPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        var projects = new Dictionary<string, ProjectInfo>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(System.IO.Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var project = Parse(path, globals);
            projects.Add(project.Name, project);
        }

        return new SourceTree(root, projects);
    }

    private static ProjectInfo Parse(string path, IReadOnlyDictionary<string, string> globals)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var document = XDocument.Load(path);
        var properties = ReadUnconditionalProperties(document, path, new Dictionary<string, string>(globals, StringComparer.OrdinalIgnoreCase));

        var tfms = new List<string>();
        properties.TryGetValue("TargetFrameworks", out var multi);
        properties.TryGetValue("TargetFramework", out var single);
        var raw = !string.IsNullOrWhiteSpace(multi) ? multi : single;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            tfms.AddRange(raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        // Conditional TargetFramework(s) values (e.g. per-OS) count too: they can only add TFMs.
        foreach (var element in document.Descendants().Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks"))
        {
            if (CombinedCondition(element) is not null)
            {
                tfms.AddRange(Expand(element.Value, properties, path).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        var references = document.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e =>
            {
                var include = (string?)e.Attribute("Include") ?? throw new InvalidOperationException($"ProjectReference without Include in {path}.");
                var fileName = include.Replace('\\', '/').Split('/')[^1];
                var condition = CombinedCondition(e);
                return new ProjectReferenceInfo(
                    System.IO.Path.GetFileNameWithoutExtension(fileName),
                    include,
                    condition is null ? null : ExpandCondition(condition, properties));
            })
            .ToArray();

        return new ProjectInfo(name, path, tfms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), references);
    }

    /// <summary>Reads properties outside any Condition, in document order, expanding <c>$(Name)</c> as MSBuild would.</summary>
    private static Dictionary<string, string> ReadUnconditionalProperties(XDocument document, string source, Dictionary<string, string> properties)
    {
        foreach (var group in document.Root!.Elements().Where(e => e.Name.LocalName == "PropertyGroup" && !HasCondition(e)))
        {
            foreach (var property in group.Elements().Where(e => !HasCondition(e)))
            {
                properties[property.Name.LocalName] = Expand(property.Value, properties, source);
            }
        }

        return properties;
    }

    private static string Expand(string value, IReadOnlyDictionary<string, string> properties, string source) =>
        PropertyPattern().Replace(value, match =>
            properties.TryGetValue(match.Groups[1].Value, out var expanded)
                ? expanded
                : throw new InvalidOperationException($"Unknown MSBuild property $({match.Groups[1].Value}) in {source}."));

    /// <summary>Expands shared properties inside a condition but keeps the per-build <c>$(TargetFramework)</c> literal.</summary>
    private static string ExpandCondition(string condition, IReadOnlyDictionary<string, string> properties) =>
        PropertyPattern().Replace(condition, match =>
            match.Groups[1].Value is not ("TargetFramework" or "TargetFrameworks") && properties.TryGetValue(match.Groups[1].Value, out var expanded)
                ? expanded
                : match.Value);

    private static bool HasCondition(XElement element) => element.Attribute("Condition") is not null;

    private static string? CombinedCondition(XElement element)
    {
        var conditions = element.AncestorsAndSelf()
            .Select(e => (string?)e.Attribute("Condition"))
            .Where(c => c is not null)
            .ToArray();
        return conditions.Length == 0 ? null : string.Join(" and ", conditions);
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Murmur.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Murmur.sln not found above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex(@"\$\(([A-Za-z_][A-Za-z0-9_.\-]*)\)")]
    private static partial Regex PropertyPattern();
}
