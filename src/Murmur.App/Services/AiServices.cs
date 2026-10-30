using Murmur.Core.Intelligence;
using Murmur.Core.Settings;

namespace Murmur.App.Services;

/// <summary>The local AI the screens use: the RAG assistant (search and chat) and the model host behind it.</summary>
/// <param name="Assistant">Hybrid search and question answering with timestamp citations.</param>
/// <param name="Llm">Only read for <see cref="ILlm.Status"/>: whether the models are installed at all.</param>
/// <param name="ModelsDirectory">Where <c>scripts/fetch-models.ps1 -Ai</c> puts the models (shown in the install hint).</param>
/// <param name="AccelerationProbe">Which accelerations this PC supports and the GPU name, for the Settings control; <c>null</c> = only the CPU build is offered.</param>
/// <param name="AccelerationSource">Which build runs and why a GPU build does not (needs download, failed to start); <c>null</c> = always the chosen CPU build.</param>
/// <param name="AccelerationRuntimeIds">The id of the model-manager item that holds the runtime of each GPU build on this platform (the items are unlisted, so the catalog does not name them); a build without an entry has nothing to download. <c>null</c> = none.</param>
public sealed record AiServices(
    IAssistant Assistant,
    ILlm Llm,
    string ModelsDirectory,
    IAccelerationProbe? AccelerationProbe = null,
    IAccelerationStatusSource? AccelerationSource = null,
    IReadOnlyDictionary<AiAcceleration, string>? AccelerationRuntimeIds = null);
