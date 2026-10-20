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

/// <summary>An alternative place to download a file from (same content under the same canonical local name).</summary>
/// <param name="Url">Absolute http(s) URL.</param>
/// <param name="Repo">Hugging Face repository, recorded in <c>gguf/models.json</c> when this mirror is the one used.</param>
/// <param name="FileName">Name of the file on that mirror (may differ from the local name), recorded in <c>models.json</c>.</param>
/// <param name="Sha256">Expected SHA-256 for this mirror; <c>null</c> when it is not known to be the same file.</param>
/// <param name="SizeBytes">Exact expected size for this mirror, or <c>null</c>.</param>
public sealed record ModelMirror(string Url, string? Repo = null, string? FileName = null, string? Sha256 = null, long? SizeBytes = null);

/// <summary>One file to download.</summary>
/// <param name="Url">Absolute http(s) URL of the first (preferred) source.</param>
/// <param name="RelativePath">
/// Path below the item's target directory (<see cref="ModelCatalogItem.IsDirectory"/>), or the file name for a single-file
/// item (or the archive file name for an archive). This is the canonical local name whichever source serves the file.
/// </param>
/// <param name="Sha256">Expected SHA-256 as hex of the first source, or <c>null</c> while it is not pinned.</param>
/// <param name="SizeBytes">Exact expected size of the first source, or <c>null</c> to trust the server's Content-Length.</param>
/// <param name="Repo">Hugging Face repository of the first source.</param>
/// <param name="RemoteName">Name of the file on the first source when it differs from <paramref name="RelativePath"/>.</param>
/// <param name="Fallbacks">Further sources, tried in order when the earlier ones do not answer.</param>
public sealed record ModelFileSpec(
    string Url,
    string RelativePath,
    string? Sha256 = null,
    long? SizeBytes = null,
    string? Repo = null,
    string? RemoteName = null,
    IReadOnlyList<ModelMirror>? Fallbacks = null)
{
    /// <summary>All sources in the order they are tried (the first source, then the fallbacks).</summary>
    public IReadOnlyList<ModelMirror> Sources =>
        [new ModelMirror(Url, Repo, RemoteName, Sha256, SizeBytes), .. Fallbacks ?? []];
}

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
/// <param name="PinHashes">Attach the pinned SHA-256 values and exact sizes; tests that serve made-up content turn it off.</param>
/// <param name="Revisions">
/// Hugging Face repository to commit SHA; a file of such a repository is downloaded from <c>resolve/&lt;sha&gt;/</c>. A
/// repository that is not listed is downloaded from <c>resolve/main</c>. <c>null</c>: <see cref="ModelCatalog.HuggingFaceRevisions"/>.
/// </param>
public sealed record ModelCatalogSources(string HuggingFace, string GitHub, bool PinHashes = true, IReadOnlyDictionary<string, string>? Revisions = null)
{
    public static ModelCatalogSources Default { get; } = new("https://huggingface.co", "https://github.com");
}
