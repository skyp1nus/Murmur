using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;

namespace Murmur.AiEval;

/// <summary>How the chat model produced the last streamed answer: why it stopped and where the tokens went.</summary>
public sealed record GenerationStats(string? FinishReason, long? InputTokens, long? OutputTokens, int ReasoningChars);

/// <summary>
/// Wraps an <see cref="ILlm"/> to record what the assistant does not report: the finish reason, the token usage
/// and the length of hidden reasoning of each streamed answer. Does not own the inner model; one question at a time.
/// </summary>
public sealed class ObservedLlm(ILlm inner) : ILlm
{
    /// <summary>Stats of the most recent streamed chat response, or <c>null</c> before the first one.</summary>
    public GenerationStats? Last { get; private set; }

    public LlmStatus Status => inner.Status;

    public async ValueTask<IChatClient> GetChatClientAsync(CancellationToken cancellationToken) =>
        new ObservedChatClient(await inner.GetChatClientAsync(cancellationToken).ConfigureAwait(false), this);

    public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(CancellationToken cancellationToken) =>
        inner.GetEmbeddingGeneratorAsync(cancellationToken);

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => inner.UnloadAsync(cancellationToken);

    /// <summary>The inner model belongs to the caller.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class ObservedChatClient(IChatClient inner, ObservedLlm owner) : DelegatingChatClient(inner)
    {
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            owner.Last = null;
            string? finish = null;
            long? input = null;
            long? output = null;
            var reasoning = 0;
            try
            {
                await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                {
                    finish = update.FinishReason?.Value ?? finish;
                    foreach (var content in update.Contents)
                    {
                        if (content is TextReasoningContent thinking)
                        {
                            reasoning += thinking.Text.Length;
                        }
                        else if (content is UsageContent usage)
                        {
                            input = usage.Details.InputTokenCount ?? input;
                            output = usage.Details.OutputTokenCount ?? output;
                        }
                    }

                    yield return update;
                }
            }
            finally
            {
                owner.Last = new GenerationStats(finish, input, output, reasoning);
            }
        }
    }
}
