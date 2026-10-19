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

        // Candidate order: what the night run actually downloaded first. The local file name is always the first one.
        items.Add(Gguf(
            sources,
            EmbeddingId,
            ModelKind.Embedding,
            "EmbeddingGemma 300M (Q8_0)",
            333_590_944,
            "embeddinggemma-300M-Q8_0.gguf",
            new GgufSource("ggml-org/embeddinggemma-300M-GGUF", "embeddinggemma-300M-Q8_0.gguf", pin ? "b5ce9d77a3fc4b3b39ccb5643c36777911cc4eb46a66962eadfa3f5f60490d63" : null, pin ? 333_590_944 : null),
            new GgufSource("unsloth/embeddinggemma-300m-GGUF", "embeddinggemma-300m-Q8_0.gguf", null, null)));
        items.Add(Gguf(
            sources,
            ChatE4BId,
            ModelKind.Chat,
            "Gemma 4 E4B (Q4_K_M)",
            5_000_000_000,
            "gemma-4-E4B-it-Q4_K_M.gguf",
            new GgufSource("unsloth/gemma-4-E4B-it-GGUF", "gemma-4-E4B-it-Q4_K_M.gguf", null, null),
            new GgufSource("bartowski/google_gemma-4-E4B-it-GGUF", "google_gemma-4-E4B-it-Q4_K_M.gguf", null, null),
            new GgufSource("ggml-org/gemma-4-E4B-it-GGUF", "gemma-4-E4B-it-Q4_K_M.gguf", null, null)));

        // UNVERIFIED names: the 26B repositories/files are guesses, the models.yml head-only step checks them.
        items.Add(Gguf(
            sources,
            Chat26BId,
            ModelKind.Chat,
            "Gemma 4 26B A4B (Q4_K_M)",
            17_000_000_000,
            "gemma-4-26B-A4B-it-Q4_K_M.gguf",
            new GgufSource("unsloth/gemma-4-26B-A4B-it-GGUF", "gemma-4-26B-A4B-it-Q4_K_M.gguf", null, null),
            new GgufSource("bartowski/google_gemma-4-26B-A4B-it-GGUF", "google_gemma-4-26B-A4B-it-Q4_K_M.gguf", null, null),
            new GgufSource("ggml-org/gemma-4-26B-A4B-it-GGUF", "gemma-4-26B-A4B-it-Q4_K_M.gguf", null, null)));
        return items;
    }

    private static ModelFileSpec ParakeetFile(ModelCatalogSources sources, string name)
    {
        // Pinned from the first real models.yml run (Hugging Face csukuangfj, main).
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
                sources.PinHashes && platform == ModelPlatform.Linux ? "8c08b19bee40d118b803d58fd8f6633ba311ce3c7f1f0138262e636f02aa11f0" : null,
                sources.PinHashes && platform == ModelPlatform.Linux ? 17_968_449 : null)],
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

    private static string HuggingFaceUrl(ModelCatalogSources sources, string repo, string file) =>
        $"{sources.HuggingFace}/{repo}/resolve/main/{file}";
}
