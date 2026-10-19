using System.Text.Json;
using System.Text.Json.Serialization;

namespace Murmur.Models.Tests.Support;

/// <summary>
/// A copy of how <c>AiModelPaths.ReadManifest</c> in Murmur.Desktop parses <c>gguf/models.json</c> (only "chat" and
/// "embed" are read; comments and trailing commas are tolerated). Murmur.Desktop must not be referenced from here.
/// </summary>
internal static class AiModelPathsReader
{
    public static ModelsManifest? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, ManifestJsonContext.Default.ModelsManifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

internal sealed record ModelsManifest(
    [property: JsonPropertyName("chat")] string? Chat,
    [property: JsonPropertyName("embed")] string? Embed);

[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(ModelsManifest))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;
