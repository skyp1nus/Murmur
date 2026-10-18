using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Murmur.AiEval.Tests;

public sealed class ObservedLlmTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Streaming_RecordsFinishReasonUsageAndReasoning_AndPassesUpdatesThrough()
    {
        var chat = new ScriptedChatClient(
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("думаю")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("ще")]),
            new ChatResponseUpdate(ChatRole.Assistant, "Відповідь [07:42]"),
            new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 900, OutputTokenCount = 512 })])
            {
                FinishReason = ChatFinishReason.Length,
            });
        var observed = new ObservedLlm(new FakeLlm(chat, new ConceptEmbeddingGenerator([])));
        Assert.Null(observed.Last);

        var client = await observed.GetChatClientAsync(Ct);
        var text = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "?")], cancellationToken: Ct))
        {
            text.Add(update.Text);
        }

        Assert.Equal("Відповідь [07:42]", string.Concat(text));
        Assert.Equal(new GenerationStats("length", 900, 512, 7), observed.Last);
    }

    [Fact]
    public async Task Streaming_WithoutUsageOrReasoning_RecordsWhatIsKnown()
    {
        var chat = new ScriptedChatClient(new ChatResponseUpdate(ChatRole.Assistant, "так") { FinishReason = ChatFinishReason.Stop });
        var observed = new ObservedLlm(new FakeLlm(chat, new ConceptEmbeddingGenerator([])));

        var client = await observed.GetChatClientAsync(Ct);
        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "?")], cancellationToken: Ct))
        {
        }

        Assert.Equal(new GenerationStats("stop", null, null, 0), observed.Last);
    }

    [Fact]
    public async Task Dispose_LeavesTheInnerModelToItsOwner()
    {
        var inner = new FakeLlm(new ScriptedChatClient(), new ConceptEmbeddingGenerator([]));
        await new ObservedLlm(inner).DisposeAsync();

        Assert.False(inner.Disposed);
    }

    private sealed class ScriptedChatClient(params ChatResponseUpdate[] updates) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
