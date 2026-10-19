using Murmur.Core.Models;

namespace Murmur.Models;

/// <summary>How the downloaded file(s) of an item become the installed layout.</summary>
public enum ModelPackaging
{
    /// <summary>Every file is installed as is, at its <see cref="ModelFileSpec.RelativePath"/> below the item's target.</summary>
    Files,

    /// <summary>A single .zip archive; the folder that contains <see cref="ModelCatalogItem.ExecutableName"/> becomes the target.</summary>
    Zip,

    /// <summary>A single .tar.gz archive; the folder that contains <see cref="ModelCatalogItem.ExecutableName"/> becomes the target.</summary>
    TarGz,
}

/// <summary>The OS the catalog is built for (the llama.cpp runtime build differs).</summary>
public enum ModelPlatform
{
    Windows,
    Linux,

    /// <summary>No llama.cpp runtime in the catalog yet (phase 5).</summary>
    Other,
}

/// <summary>One file to download.</summary>
/// <param name="Url">Absolute http(s) URL.</param>
/// <param name="RelativePath">
/// Path below the item's target directory (<see cref="ModelCatalogItem.IsDirectory"/>), or the file name for a single-file
/// item (or the archive file name for an archive).
/// </param>
/// <param name="Sha256">Expected SHA-256 as hex, or <c>null</c> while it is not pinned.</param>
/// <param name="SizeBytes">Exact expected size, or <c>null</c> to trust the server's Content-Length.</param>
public sealed record ModelFileSpec(string Url, string RelativePath, string? Sha256 = null, long? SizeBytes = null);

/// <summary>A catalog entry: what <see cref="IModelManager"/> shows plus how to download and install it.</summary>
/// <param name="Info">What the UI sees. <see cref="ModelInfo.SizeBytes"/> is an approximate display value.</param>
/// <param name="TargetPath">Where the item lives, relative to the models directory (forward slashes).</param>
/// <param name="IsDirectory"><c>true</c>: <paramref name="TargetPath"/> is a folder owned by this item and replaced as a whole.</param>
/// <param name="Files">The downloads.</param>
/// <param name="Packaging">What to do with the downloads.</param>
/// <param name="ExecutableName">For archives: the file whose folder is installed (<c>llama-server</c>).</param>
/// <param name="Repo">Hugging Face repository, recorded in <c>gguf/models.json</c> for chat and embedding models.</param>
public sealed record ModelCatalogItem(
    ModelInfo Info,
    string TargetPath,
    bool IsDirectory,
    IReadOnlyList<ModelFileSpec> Files,
    ModelPackaging Packaging = ModelPackaging.Files,
    string? ExecutableName = null,
    string? Repo = null)
{
    public string Id => Info.Id;
}

/// <summary>Base addresses of the hosts the catalog downloads from; tests point them at a local server.</summary>
/// <param name="HuggingFace">Hugging Face, without trailing slash.</param>
/// <param name="GitHub">GitHub (release downloads), without trailing slash.</param>
public sealed record ModelCatalogSources(string HuggingFace, string GitHub)
{
    public static ModelCatalogSources Default { get; } = new("https://huggingface.co", "https://github.com");
}
