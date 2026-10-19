using Murmur.Core.Models;
using Murmur.Core.Transcription;

namespace Murmur.Models;

/// <summary>
/// The pinned catalog (data in code). Sizes are approximate display values; the real total comes from the server.
/// SHA-256 values are not pinned yet: <c>tools/Murmur.ModelFetch</c> (workflow <c>models.yml</c>) prints them.
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

        var items = new List<ModelCatalogItem>
        {
            // The Hugging Face repo is the sherpa-onnx export; no archive (.NET has no bzip2).
            new(
                new ModelInfo(ParakeetId, ModelKind.Asr, "Parakeet TDT 0.6B v3 (int8)", 670_000_000, Required: true),
                ModelLocator.ParakeetDirectoryName,
                IsDirectory: true,
                [.. ModelLocator.ParakeetFiles.Select(name => new ModelFileSpec(HuggingFaceUrl(sources, "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", name), name))]),
            new(
                new ModelInfo(SileroVadId, ModelKind.Vad, "Silero VAD", 2_000_000, Required: true),
                ModelLocator.VadFileName,
                IsDirectory: false,
                [new ModelFileSpec($"{sources.GitHub}/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx", ModelLocator.VadFileName)]),
        };

        if (RuntimeArchive(sources, platform) is { } runtime)
        {
            items.Add(runtime);
        }

        items.Add(Gguf(sources, EmbeddingId, ModelKind.Embedding, "EmbeddingGemma 300M (Q8_0)", 314_000_000, "ggml-org/embeddinggemma-300M-GGUF", "embeddinggemma-300M-Q8_0.gguf"));
        items.Add(Gguf(sources, ChatE4BId, ModelKind.Chat, "Gemma 4 E4B (Q4_K_M)", 5_000_000_000, "ggml-org/gemma-4-E4B-it-GGUF", "gemma-4-E4B-it-Q4_K_M.gguf"));

        // UNVERIFIED name: the 26B repository/file is a guess, the models.yml head-only step checks it.
        items.Add(Gguf(sources, Chat26BId, ModelKind.Chat, "Gemma 4 26B A4B (Q4_K_M)", 17_000_000_000, "ggml-org/gemma-4-26B-A4B-it-GGUF", "gemma-4-26B-A4B-it-Q4_K_M.gguf"));
        return items;
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
            [new ModelFileSpec($"{sources.GitHub}/ggml-org/llama.cpp/releases/download/{LlamaCppTag}/{archive}", archive)],
            packaging,
            executable);
    }

    private static ModelCatalogItem Gguf(ModelCatalogSources sources, string id, ModelKind kind, string displayName, long size, string repo, string file) =>
        new(
            new ModelInfo(id, kind, displayName, size, Required: false),
            $"{GgufDirectoryName}/{file}",
            IsDirectory: false,
            [new ModelFileSpec(HuggingFaceUrl(sources, repo, file), file)],
            Repo: repo);

    private static string HuggingFaceUrl(ModelCatalogSources sources, string repo, string file) =>
        $"{sources.HuggingFace}/{repo}/resolve/main/{file}";
}
