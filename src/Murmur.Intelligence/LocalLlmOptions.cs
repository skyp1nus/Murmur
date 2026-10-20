using Murmur.Core.Settings;

namespace Murmur.Intelligence;

/// <summary>Configuration of <see cref="LocalLlm"/>: which GGUF models and which <c>llama-server</c> binary to run.</summary>
public sealed record LocalLlmOptions
{
    /// <summary>Default chat model file name inside the models folder (Gemma 4 E4B, Q4).</summary>
    public const string DefaultChatModelFileName = "gemma-4-E4B-it-Q4_K_M.gguf";

    /// <summary>Default embedding model file name inside the models folder (EmbeddingGemma 300M).</summary>
    public const string DefaultEmbeddingModelFileName = "embeddinggemma-300M-Q8_0.gguf";

    /// <summary>Half of the logical processors (≈ physical cores with SMT), at least one.</summary>
    public static int DefaultThreads => Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary><c>%LOCALAPPDATA%/Murmur/models</c> (or <c>~/.local/share/Murmur/models</c>).</summary>
    public static string DefaultModelsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Murmur", "models");

    /// <summary>Gemma 4 E4B GGUF (Q4) for chat; <c>null</c> when AI was skipped.</summary>
    public string? ChatModelPath { get; init; }

    /// <summary>EmbeddingGemma GGUF; <c>null</c> when AI was skipped.</summary>
    public string? EmbeddingModelPath { get; init; }

    /// <summary>Path of the CPU build of <c>llama-server(.exe)</c>. Always needed: it is also the fallback of a GPU build.</summary>
    public string? LlamaServerPath { get; init; }

    /// <summary>The build the user chose. Anything but <see cref="AiAcceleration.Cpu"/> needs <see cref="AcceleratedServerPath"/>.</summary>
    public AiAcceleration Acceleration { get; init; } = AiAcceleration.Cpu;

    /// <summary>
    /// Path of <c>llama-server(.exe)</c> of the chosen GPU build (<see cref="DefaultServerPath(string, AiAcceleration)"/>).
    /// When the file is missing the CPU build runs and <see cref="LocalLlm.Current"/> reports that the runtime needs to be downloaded.
    /// </summary>
    public string? AcceleratedServerPath { get; init; }

    /// <summary>
    /// Arguments added to the chat server of a GPU build: offload every layer (<c>-ngl 99</c>). Placed before <see cref="ChatExtraArguments"/>, so those can override them.
    /// Risk: a model larger than the VRAM falls back to the CPU build instead of a partial offload; revisit with <c>-ngl auto</c> / fit once verified on b11716.
    /// </summary>
    public IReadOnlyList<string> AcceleratedChatArguments { get; init; } = ["-ngl", "99"];

    /// <summary>
    /// Run the embedding server on the GPU build too. Off by default: EmbeddingGemma 300M (Q8, ~330 MB) embeds a
    /// session on the CPU in the background, a second GPU context would compete with the chat model for VRAM and add a
    /// driver start-up to every indexing run, and the CPU server cannot be taken down by a GPU driver problem.
    /// </summary>
    public bool AccelerateEmbeddings { get; init; }

    /// <summary>Arguments added to the embedding server when <see cref="AccelerateEmbeddings"/> is on and the GPU build runs.</summary>
    public IReadOnlyList<string> AcceleratedEmbeddingArguments { get; init; } = ["-ngl", "99"];

    /// <summary>CPU threads per server (<c>-t</c>).</summary>
    public int Threads { get; init; } = DefaultThreads;

    /// <summary>Chat context size in tokens (<c>-c</c>).</summary>
    public int ContextSize { get; init; } = 8192;

    /// <summary>Embedding server context size in tokens; split between its parallel slots.</summary>
    public int EmbeddingContextSize { get; init; } = 8192;

    /// <summary>Extra <c>llama-server</c> arguments for the chat server (e.g. <c>-ngl 99</c> on a GPU build).</summary>
    public IReadOnlyList<string> ChatExtraArguments { get; init; } = [];

    /// <summary>
    /// Extra arguments for the embedding server. EmbeddingGemma attends bidirectionally, so one input must fit in a
    /// single physical batch: the default raises <c>-b</c>/<c>-ub</c> from 512 to its 2048-token window.
    /// </summary>
    public IReadOnlyList<string> EmbeddingExtraArguments { get; init; } = ["-b", "2048", "-ub", "2048"];

    /// <summary>Inputs per <c>/v1/embeddings</c> request.</summary>
    public int EmbeddingBatchSize { get; init; } = 16;

    /// <summary>A server that served nothing for this long is stopped.</summary>
    public TimeSpan IdleUnload { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a server may take to load its model.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Reads <c>MURMUR_MODELS_DIR</c> (default <see cref="DefaultModelsDirectory"/>), <c>MURMUR_LLM_MODEL</c>,
    /// <c>MURMUR_EMBED_MODEL</c> and <c>MURMUR_LLAMA_SERVER</c>; unset paths fall back to files in the models folder.
    /// </summary>
    public static LocalLlmOptions FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    /// <inheritdoc cref="FromEnvironment()"/>
    public static LocalLlmOptions FromEnvironment(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        var modelsDirectory = NonEmpty(getVariable("MURMUR_MODELS_DIR")) ?? DefaultModelsDirectory;
        return new LocalLlmOptions
        {
            ChatModelPath = NonEmpty(getVariable("MURMUR_LLM_MODEL")) ?? Path.Combine(modelsDirectory, DefaultChatModelFileName),
            EmbeddingModelPath = NonEmpty(getVariable("MURMUR_EMBED_MODEL")) ?? Path.Combine(modelsDirectory, DefaultEmbeddingModelFileName),
            LlamaServerPath = NonEmpty(getVariable("MURMUR_LLAMA_SERVER")) ?? DefaultServerPath(modelsDirectory),
        };
    }

    /// <summary><c>&lt;models&gt;/llama.cpp/llama-server(.exe)</c>.</summary>
    public static string DefaultServerPath(string modelsDirectory) => DefaultServerPath(modelsDirectory, AiAcceleration.Cpu);

    /// <summary>
    /// Server of a build: <c>&lt;models&gt;/llama.cpp[-vulkan|-cuda]/llama-server(.exe)</c>. Each build has its own folder
    /// (the folder names are the <c>ModelCatalog</c> targets), so installing a GPU build leaves the CPU build intact.
    /// </summary>
    public static string DefaultServerPath(string modelsDirectory, AiAcceleration acceleration) =>
        Path.Combine(modelsDirectory, ServerDirectoryName(acceleration), OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");

    /// <summary>Folder (below the models folder) holding the build: <c>llama.cpp</c>, <c>llama.cpp-vulkan</c> or <c>llama.cpp-cuda</c>.</summary>
    public static string ServerDirectoryName(AiAcceleration acceleration) => acceleration switch
    {
        AiAcceleration.Vulkan => "llama.cpp-vulkan",
        AiAcceleration.Cuda => "llama.cpp-cuda",
        _ => "llama.cpp",
    };

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
