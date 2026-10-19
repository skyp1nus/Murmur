using System.Text.Json.Serialization;

namespace Murmur.ModelFetch;

// Wire formats of the three APIs --resolve-pins reads (only the fields it needs) and of the JSON it prints.

/// <summary><c>GET /api/models/{repo}/revision/main</c>.</summary>
internal sealed class HfRevisionResponse
{
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }
}

/// <summary>Body of <c>POST /api/models/{repo}/paths-info/{sha}</c>.</summary>
internal sealed class HfPathsRequest
{
    [JsonPropertyName("paths")]
    public List<string> Paths { get; set; } = [];
}

/// <summary>One element of the paths-info answer.</summary>
internal sealed class HfPathInfo
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    /// <summary>Present for files stored in LFS; <see cref="HfLfsInfo.Oid"/> is their SHA-256.</summary>
    [JsonPropertyName("lfs")]
    public HfLfsInfo? Lfs { get; set; }
}

internal sealed class HfLfsInfo
{
    [JsonPropertyName("oid")]
    public string? Oid { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }
}

/// <summary><c>GET /repos/{owner}/{repo}/releases/tags/{tag}</c> (<c>assets</c> may be a truncated list: see the assets endpoint).</summary>
internal sealed class GitHubRelease
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    /// <summary><c>sha256:&lt;hex&gt;</c>; missing on assets that were uploaded long ago.</summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

/// <summary>One line of the JSON the tool prints: what upstream says about one source of one file.</summary>
internal sealed class PinRow
{
    [JsonPropertyName("item")]
    public string Item { get; set; } = "";

    /// <summary><c>repo</c> for Hugging Face, <c>owner/repo</c> for GitHub.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    /// <summary>Commit SHA the values were read at (Hugging Face: the catalog revision, or the head of main when the catalog uses main) or the release tag (GitHub).</summary>
    [JsonPropertyName("revision")]
    public string? Revision { get; set; }

    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    /// <summary><c>match</c>, <c>unpinned</c>, <c>DRIFT</c> or <c>ERROR</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>A single-file GGUF source (its catalog entry is a <c>GgufSource</c>); only for the C# snippet.</summary>
    [JsonIgnore]
    public bool IsGguf { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(HfRevisionResponse))]
[JsonSerializable(typeof(HfPathsRequest))]
[JsonSerializable(typeof(List<HfPathInfo>))]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(List<GitHubAsset>))]
[JsonSerializable(typeof(List<PinRow>))]
internal sealed partial class PinJsonContext : JsonSerializerContext;
