using Murmur.Core.Models;
using Murmur.Core.Transcription;

namespace Murmur.Models;

/// <summary>
/// The pinned catalog (data in code). Display sizes of the GGUF items are the size of their first source; the real total comes from the server.
/// SHA-256 values and sizes are pinned where known; <c>tools/Murmur.ModelFetch --resolve-pins</c> (workflow <c>models.yml</c>)
/// prints the current upstream values, the commit of every Hugging Face repository and whether a pin has drifted.
/// </summary>
/// <remarks>
/// The AI items (<c>llama-cpp-*</c>, <c>embeddinggemma-*</c> and one <c>gemma-4-*</c>) are useful only together:
/// the UI installs the runtime, the embedding model and one chat model as a single choice.
/// </remarks>
public static class ModelCatalog
{
    public const string ParakeetId = "parakeet-tdt-0.6b-v3-int8";
    public const string SileroVadId = "silero-vad";
    public const string LlamaCppTag = "b11716";
    public const string LlamaCppId = "llama-cpp-" + LlamaCppTag;
    public const string EmbeddingId = "embeddinggemma-300m-q8";
    public const string ChatE4BId = "gemma-4-e4b-q4";
    public const string Chat26BId = "gemma-4-26b-a4b-q4";

    /// <summary>
    /// Hugging Face repository to the commit its pinned hashes belong to. A hash pinned on <c>resolve/main</c> would break
    /// every install for good the day upstream re-uploads a file, so a repository listed here is downloaded from
    /// <c>resolve/&lt;sha&gt;</c>. A repository that is not listed yet is downloaded from <c>main</c>: fill the value in from
    /// the <c>--resolve-pins</c> output of the models workflow (and re-pin the hashes of that repository from the same run).
    /// </summary>
    public static IReadOnlyDictionary<string, string> HuggingFaceRevisions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Resolved by `ModelFetch --resolve-pins` (models.yml run 39309895708); every pinned hash below belongs to these commits.
        ["bartowski/google_gemma-4-26B-A4B-it-GGUF"] = "10f3b41bcf8d3047f4e136e7197ffc2dd1654c9d",
        ["bartowski/google_gemma-4-E4B-it-GGUF"] = "029e94146666900b08caf49a3b47b413dfa8ec66",
        ["csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8"] = "2bda32ec70b097a55adaa07d9a7173915b43cc78",
        ["ggml-org/embeddinggemma-300M-GGUF"] = "0f741b5a6585bd53aeb15cd1372c56f2a0f65e12",
        ["unsloth/gemma-4-E4B-it-GGUF"] = "bfc15c382204943c3a8fff0c750b94ae2364d7a3",
    };

    /// <summary>Directory (below the models directory) holding the llama.cpp server and its libraries.</summary>
    public const string LlamaCppDirectoryName = "llama.cpp";

    /// <summary>Directory (below the models directory) holding the GGUF files and <c>models.json</c>.</summary>
    public const string GgufDirectoryName = "gguf";

    /// <summary>The catalog for the OS this process runs on, from the real hosts.</summary>
    public static IReadOnlyList<ModelCatalogItem> ForCurrentOs() => Create(ModelCatalogSources.Default, CurrentPlatform());

    public static ModelPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? ModelPlatform.Windows : OperatingSystem.IsLinux() ? ModelPlatform.Linux : ModelPlatform.Other;

    /// <summary>Items in display order (required first).</summary>
    public static IReadOnlyList<ModelCatalogItem> Create(ModelCatalogSources sources, ModelPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var pin = sources.PinHashes;
        var items = new List<ModelCatalogItem>
        {
            // The Hugging Face repo is the sherpa-onnx export; no archive (.NET has no bzip2).
            new(
                new ModelInfo(ParakeetId, ModelKind.Asr, "Parakeet TDT 0.6B v3 (int8)", 670_478_772, Required: true),
                ModelLocator.ParakeetDirectoryName,
                IsDirectory: true,
                [.. ModelLocator.ParakeetFiles.Select(name => ParakeetFile(sources, name))]),
            new(
                new ModelInfo(SileroVadId, ModelKind.Vad, "Silero VAD", 643_854, Required: true),
                ModelLocator.VadFileName,
                IsDirectory: false,
                [new ModelFileSpec(
                    $"{sources.GitHub}/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx",
                    ModelLocator.VadFileName,
                    pin ? "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6" : null,
                    pin ? 643_854 : null)]),
        };

        if (RuntimeArchive(sources, platform) is { } runtime)
        {
            items.Add(runtime);
        }

        // Candidate order: the first source is the preferred one; the rest are fallbacks. Every source carries its own pin
        // (the same quantisation from different publishers is a different file). Mirrors that do not exist upstream were
        // removed after the first --resolve-pins run. The display size is the first source's size.
        items.Add(Gguf(
            sources,
            EmbeddingId,
            ModelKind.Embedding,
            "EmbeddingGemma 300M (Q8_0)",
            333_590_944,
            "embeddinggemma-300M-Q8_0.gguf",
            new GgufSource("ggml-org/embeddinggemma-300M-GGUF", "embeddinggemma-300M-Q8_0.gguf", pin ? "b5ce9d77a3fc4b3b39ccb5643c36777911cc4eb46a66962eadfa3f5f60490d63" : null, pin ? 333_590_944 : null)));
        items.Add(Gguf(
            sources,
            ChatE4BId,
            ModelKind.Chat,
            "Gemma 4 E4B (Q4_K_M)",
            4_977_171_584,
            "gemma-4-E4B-it-Q4_K_M.gguf",
            new GgufSource("unsloth/gemma-4-E4B-it-GGUF", "gemma-4-E4B-it-Q4_K_M.gguf", pin ? "85a896a047553e842f25297ee5b031d64ff30147d9c4af17b1e4b394cd1fab87" : null, pin ? 4_977_171_584 : null),
            new GgufSource("bartowski/google_gemma-4-E4B-it-GGUF", "google_gemma-4-E4B-it-Q4_K_M.gguf", pin ? "d35a3aa7a6d47de237fd488bb6f7f3efd62908d828f85a63da0ee93acdbefe9f" : null, pin ? 5_405_170_144 : null)));
        items.Add(Gguf(
            sources,
            Chat26BId,
            ModelKind.Chat,
            "Gemma 4 26B A4B (Q4_K_M)",
            17_035_039_872,
            "gemma-4-26B-A4B-it-Q4_K_M.gguf",
            new GgufSource("bartowski/google_gemma-4-26B-A4B-it-GGUF", "google_gemma-4-26B-A4B-it-Q4_K_M.gguf", pin ? "a07f72221e8e3f77455ab0d7f7652d01a9f63c262b954aa6932a53275a0e895a" : null, pin ? 17_035_039_872 : null)));
        return items;
    }

    private static ModelFileSpec ParakeetFile(ModelCatalogSources sources, string name)
    {
        // Pinned from the models.yml run that resolved HuggingFaceRevisions (csukuangfj commit 2bda32ec).
        (string Sha256, long Size)? pin = !sources.PinHashes ? null : name switch
        {
            "encoder.int8.onnx" => ("acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247", 652_184_281),
            "decoder.int8.onnx" => ("179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e", 11_845_275),
            "joiner.int8.onnx" => ("3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3", 6_355_277),
            "tokens.txt" => ("d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d", 93_939),
            _ => null,
        };
        return new ModelFileSpec(
            HuggingFaceUrl(sources, "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", name),
            name,
            pin?.Sha256,
            pin?.Size,
            Repo: "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8");
    }

    private static ModelCatalogItem? RuntimeArchive(ModelCatalogSources sources, ModelPlatform platform)
    {
        var (archive, packaging, executable) = platform switch
        {
            ModelPlatform.Windows => ($"llama-{LlamaCppTag}-bin-win-cpu-x64.zip", ModelPackaging.Zip, "llama-server.exe"),
            ModelPlatform.Linux => ($"llama-{LlamaCppTag}-bin-ubuntu-x64.tar.gz", ModelPackaging.TarGz, "llama-server"),
            _ => (null, ModelPackaging.Files, null),
        };

        if (archive is null)
        {
            return null;
        }

        return new ModelCatalogItem(
            new ModelInfo(LlamaCppId, ModelKind.LlmRuntime, $"llama.cpp {LlamaCppTag} (CPU server)", 30_000_000, Required: false),
            LlamaCppDirectoryName,
            IsDirectory: true,
            [new ModelFileSpec(
                $"{sources.GitHub}/ggml-org/llama.cpp/releases/download/{LlamaCppTag}/{archive}",
                archive,
                sources.PinHashes ? (platform == ModelPlatform.Linux ? "8c08b19bee40d118b803d58fd8f6633ba311ce3c7f1f0138262e636f02aa11f0" : "79b143075ab87fb5aff6f8f8ca80500a984f4120b5199fa9f9b70b284f17428e") : null,
                sources.PinHashes ? (platform == ModelPlatform.Linux ? 17_968_449 : 19_662_702) : null)],
            packaging,
            executable);
    }

    private sealed record GgufSource(string Repo, string FileName, string? Sha256, long? SizeBytes);

    /// <summary>A single-file GGUF item. <paramref name="localName"/> is the canonical name under <c>gguf/</c>.</summary>
    private static ModelCatalogItem Gguf(ModelCatalogSources sources, string id, ModelKind kind, string displayName, long size, string localName, GgufSource first, params GgufSource[] fallbacks) =>
        new(
            new ModelInfo(id, kind, displayName, size, Required: false),
            $"{GgufDirectoryName}/{localName}",
            IsDirectory: false,
            [new ModelFileSpec(
                HuggingFaceUrl(sources, first.Repo, first.FileName),
                localName,
                first.Sha256,
                first.SizeBytes,
                first.Repo,
                first.FileName,
                [.. fallbacks.Select(f => new ModelMirror(HuggingFaceUrl(sources, f.Repo, f.FileName), f.Repo, f.FileName, f.Sha256, f.SizeBytes))])],
            Repo: first.Repo);

    private static string HuggingFaceUrl(ModelCatalogSources sources, string repo, string file)
    {
        var revisions = sources.Revisions ?? HuggingFaceRevisions;
        return $"{sources.HuggingFace}/{repo}/resolve/{(revisions.TryGetValue(repo, out var revision) ? revision : "main")}/{file}";
    }
}
