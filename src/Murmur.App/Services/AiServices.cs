using Murmur.Core.Intelligence;

namespace Murmur.App.Services;

/// <summary>The local AI the screens use: the RAG assistant (search and chat) and the model host behind it.</summary>
/// <param name="Assistant">Hybrid search and question answering with timestamp citations.</param>
/// <param name="Llm">Only read for <see cref="ILlm.Status"/>: whether the models are installed at all.</param>
/// <param name="ModelsDirectory">Where <c>scripts/fetch-models.ps1 -Ai</c> puts the models (shown in the install hint).</param>
public sealed record AiServices(IAssistant Assistant, ILlm Llm, string ModelsDirectory);
