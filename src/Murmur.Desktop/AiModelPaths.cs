using System.Text.Json;
using System.Text.Json.Serialization;
using Murmur.Intelligence;

namespace Murmur.Desktop;

/// <summary>
/// Where the local AI lives, following the <c>scripts/fetch-models.ps1 -Ai</c> layout:
/// <c>&lt;models&gt;/llama.cpp/llama-server(.exe)</c> and <c>&lt;models&gt;/gguf/models.json</c>
/// (<c>{ "chat": "&lt;path&gt;", "embed": "&lt;path&gt;", ... }</c>) naming the downloaded GGUF files.
/// </summary>
/// <remarks>
/// Order per path: the environment variable (<c>MURMUR_LLM_MODEL</c>, <c>MURMUR_EMBED_MODEL</c>,
/// <c>MURMUR_LLAMA_SERVER</c>), then the manifest, then the default file name in <c>&lt;models&gt;/gguf/</c> when that
/// file exists, then <see cref="LocalLlmOptions.FromEnvironment(Func{string, string?})"/>'s default
/// (<c>&lt;models&gt;/&lt;file&gt;</c>).
/// </remarks>
internal static class AiModelPaths
{
    public const string GgufDirectoryName = "gguf";

    public const string ManifestFileName = "models.json";

    /// <param name="modelsDirectory">The models folder (the same one the ASR uses).</param>
    /// <param name="getVariable">Environment lookup.</param>
    public static LocalLlmOptions Resolve(string modelsDirectory, Func<string, string?> getVariable)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelsDirectory);
        ArgumentNullException.ThrowIfNull(getVariable);

        // FromEnvironment reads MURMUR_MODELS_DIR itself; pin it to the folder the rest of the app uses.
        var options = LocalLlmOptions.FromEnvironment(name => name == "MURMUR_MODELS_DIR" ? modelsDirectory : getVariable(name));
        var gguf = Path.Combine(modelsDirectory, GgufDirectoryName);
        var manifest = ReadManifest(Path.Combine(gguf, ManifestFileName));

        return options with
        {
            ChatModelPath = Pick(getVariable("MURMUR_LLM_MODEL"), manifest?.Chat, Path.Combine(gguf, LocalLlmOptions.DefaultChatModelFileName), options.ChatModelPath),
            EmbeddingModelPath = Pick(getVariable("MURMUR_EMBED_MODEL"), manifest?.Embed, Path.Combine(gguf, LocalLlmOptions.DefaultEmbeddingModelFileName), options.EmbeddingModelPath),
        };
    }

    /// <summary>The manifest, or <c>null</c> when it is missing or unreadable (AI then counts as not installed).</summary>
    internal static ModelsManifest? ReadManifest(string path)
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

    private static string? Pick(string? variable, string? manifest, string ggufDefault, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(variable))
        {
            return variable;
        }

        if (!string.IsNullOrWhiteSpace(manifest))
        {
            return manifest;
        }

        return File.Exists(ggufDefault) ? ggufDefault : fallback;
    }
}

/// <summary><c>&lt;models&gt;/gguf/models.json</c> as written by fetch-models (only the two paths are read).</summary>
internal sealed record ModelsManifest(
    [property: JsonPropertyName("chat")] string? Chat,
    [property: JsonPropertyName("embed")] string? Embed);

[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(ModelsManifest))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;
