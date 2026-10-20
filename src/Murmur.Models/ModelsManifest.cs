using System.Text.Json;
using System.Text.Json.Serialization;
using Murmur.Core.Models;

namespace Murmur.Models;

/// <summary>
/// <c>&lt;models&gt;/gguf/models.json</c>: <c>{ "chat": "&lt;abs path&gt;", "embed": "&lt;abs path&gt;", "repo": {...}, "file": {...} }</c>,
/// the format <c>scripts/fetch-models.ps1</c> writes and <c>AiModelPaths</c> in Murmur.Desktop reads (only the two paths).
/// </summary>
internal sealed class ManifestDocument
{
    [JsonPropertyName("chat")]
    public string? Chat { get; set; }

    [JsonPropertyName("embed")]
    public string? Embed { get; set; }

    [JsonPropertyName("repo")]
    public ManifestPair? Repo { get; set; }

    [JsonPropertyName("file")]
    public ManifestPair? File { get; set; }
}

internal sealed class ManifestPair
{
    [JsonPropertyName("chat")]
    public string? Chat { get; set; }

    [JsonPropertyName("embed")]
    public string? Embed { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Chat is null && Embed is null;
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ManifestDocument))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;

/// <summary>Reads and updates the manifest; all access is serialized, writes are atomic (temp file + rename).</summary>
internal sealed class ModelsManifestStore
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly object _lock = new();

    public ModelsManifestStore(string modelsDirectory)
    {
        ManifestPath = Path.GetFullPath(Path.Combine(modelsDirectory, ModelCatalog.GgufDirectoryName, "models.json"));
    }

    public string ManifestPath { get; }

    /// <summary>The active file of a chat or embedding model, or <c>null</c>.</summary>
    public string? ActivePath(ModelKind kind)
    {
        lock (_lock)
        {
            var doc = Read();
            return kind == ModelKind.Chat ? doc.Chat : doc.Embed;
        }
    }

    public bool IsActive(ModelKind kind, string absolutePath) =>
        ActivePath(kind) is { Length: > 0 } active && string.Equals(active, absolutePath, PathComparison);

    /// <summary>
    /// Makes the file the active model of its kind, keeping the other kind's entries. With
    /// <paramref name="keepRecordedSource"/> the repo and file already recorded for the same path are left as they are
    /// (re-activating an installed model must not forget which mirror it came from).
    /// </summary>
    public void SetActive(ModelKind kind, string absolutePath, string? repo, string fileName, bool keepRecordedSource = false)
    {
        lock (_lock)
        {
            var doc = Read();
            if (keepRecordedSource && string.Equals(kind == ModelKind.Chat ? doc.Chat : doc.Embed, absolutePath, PathComparison) && (kind == ModelKind.Chat ? doc.File?.Chat : doc.File?.Embed) is not null)
            {
                return;
            }

            doc.Repo ??= new ManifestPair();
            doc.File ??= new ManifestPair();
            if (kind == ModelKind.Chat)
            {
                doc.Chat = absolutePath;
                doc.Repo.Chat = repo;
                doc.File.Chat = fileName;
            }
            else
            {
                doc.Embed = absolutePath;
                doc.Repo.Embed = repo;
                doc.File.Embed = fileName;
            }

            Write(doc);
        }
    }

    /// <summary>Clears the entry of <paramref name="kind"/> when it names <paramref name="absolutePath"/>.</summary>
    public void ClearIfActive(ModelKind kind, string absolutePath)
    {
        lock (_lock)
        {
            if (!File.Exists(ManifestPath))
            {
                return;
            }

            var doc = Read();
            var active = kind == ModelKind.Chat ? doc.Chat : doc.Embed;
            if (active is null || !string.Equals(active, absolutePath, PathComparison))
            {
                return;
            }

            if (kind == ModelKind.Chat)
            {
                doc.Chat = null;
                if (doc.Repo is not null)
                {
                    doc.Repo.Chat = null;
                }

                if (doc.File is not null)
                {
                    doc.File.Chat = null;
                }
            }
            else
            {
                doc.Embed = null;
                if (doc.Repo is not null)
                {
                    doc.Repo.Embed = null;
                }

                if (doc.File is not null)
                {
                    doc.File.Embed = null;
                }
            }

            Write(doc);
        }
    }

    /// <summary>The manifest, or an empty one when it is missing or unreadable.</summary>
    private ManifestDocument Read()
    {
        try
        {
            if (!File.Exists(ManifestPath))
            {
                return new ManifestDocument();
            }

            using var stream = File.OpenRead(ManifestPath);
            return JsonSerializer.Deserialize(stream, ManifestJsonContext.Default.ManifestDocument) ?? new ManifestDocument();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ManifestDocument();
        }
    }

    private void Write(ManifestDocument doc)
    {
        if (doc.Repo is { IsEmpty: true })
        {
            doc.Repo = null;
        }

        if (doc.File is { IsEmpty: true })
        {
            doc.File = null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        var part = ManifestPath + ".part";
        using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, doc, ManifestJsonContext.Default.ManifestDocument);
        }

        File.Move(part, ManifestPath, overwrite: true);
    }
}
