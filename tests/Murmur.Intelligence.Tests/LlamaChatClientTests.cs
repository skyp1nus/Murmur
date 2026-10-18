using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Murmur.Intelligence.Llama;
using Murmur.Intelligence.Tests.Fakes;

namespace Murmur.Intelligence.Tests;

public sealed class LlamaChatClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetResponse_MapsOptionsAndParsesTheCompletion()
    {
        await using var server = new FakeOpenAiServer((_, response) => FakeOpenAiServer.WriteJsonAsync(response, """
            {"id":"chatcmpl-1","object":"chat.completion","created":1700000000,"model":"gemma-4",
             "choices":[{"index":0,"message":{"role":"assistant","content":"Привіт [00:12]"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":11,"completion_tokens":5,"total_tokens":16}}
            """));
        using var client = new LlamaChatClient(server.BaseAddress, modelId: "gemma-4");

        var options = new ChatOptions
        {
            Temperature = 0.25f,
            MaxOutputTokens = 64,
            StopSequences = ["\n\n", "</s>"],
            TopP = 0.9f,
            Instructions = "Be brief.",
        };
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "sys"), new ChatMessage(ChatRole.User, "Що сказали?")], options, Ct);

        Assert.Equal("Привіт [00:12]", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal("gemma-4", response.ModelId);
        Assert.Equal("chatcmpl-1", response.ResponseId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), response.CreatedAt);
        Assert.Equal(11, response.Usage?.InputTokenCount);
        Assert.Equal(5, response.Usage?.OutputTokenCount);
        Assert.Equal(16, response.Usage?.TotalTokenCount);

        var request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/chat/completions", request.Path);
        var json = request.Json;
        Assert.Equal("gemma-4", json.GetProperty("model").GetString());
        Assert.False(json.GetProperty("stream").GetBoolean());
        Assert.Equal(0.25, json.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.9, json.GetProperty("top_p").GetDouble(), 3);
        Assert.Equal(64, json.GetProperty("max_tokens").GetInt32());
        Assert.Equal(["\n\n", "</s>"], json.GetProperty("stop").EnumerateArray().Select(e => e.GetString()!));
        Assert.False(json.TryGetProperty("top_k", out _));
        Assert.False(json.TryGetProperty("stream_options", out _));

        var messages = json.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(3, messages.Length);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("Be brief.", messages[0].GetProperty("content").GetString());
        Assert.Equal("system", messages[1].GetProperty("role").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal("Що сказали?", messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetResponse_WithoutOptions_OmitsSamplingFields()
    {
        await using var server = new FakeOpenAiServer((_, response) => FakeOpenAiServer.WriteJsonAsync(response,
            """{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"length"}]}"""));
        using var client = new LlamaChatClient(server.BaseAddress);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, Ct);

        Assert.Equal("ok", response.Text);
        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
        var json = Assert.Single(server.Requests).Json;
        foreach (var name in new[] { "model", "temperature", "top_p", "max_tokens", "stop", "chat_template_kwargs" })
        {
            Assert.False(json.TryGetProperty(name, out _), name);
        }
    }

    [Fact]
    public async Task GetStreamingResponse_ReassemblesSplitServerSentEvents()
    {
        const string sse =
            "data: {\"id\":\"s1\",\"model\":\"gemma-4\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}\n\n"
            + ": keep-alive comment\n\n"
            + "data: {\"id\":\"s1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Привіт\"}}]}\n\n"
            + "data:{\"id\":\"s1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\", світ\"}}]}\r\n\r\n"
            + "data: {\"id\":\"s1\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"thinking\"}}]}\n\n"
            + "data: {\"id\":\"s1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: {\"id\":\"s1\",\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}\n\n"
            + "data: [DONE]\n\n"
            + "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"AFTER DONE\"}}]}\n\n";

        // Cut inside "data:", inside the JSON, inside the two-byte UTF-8 letter "П" and inside "[DONE]".
        var bytes = System.Text.Encoding.UTF8.GetBytes(sse);
        var letter = IndexOf(bytes, System.Text.Encoding.UTF8.GetBytes("Привіт")) + 1;
        var done = IndexOf(bytes, "[DONE]"u8.ToArray()) + 3;
        await using var server = new FakeOpenAiServer((_, response) =>
            FakeOpenAiServer.WriteSseAsync(response, FakeOpenAiServer.Split(sse, 3, 40, letter, letter + 5, done)));
        using var client = new LlamaChatClient(server.BaseAddress);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { MaxOutputTokens = 8 }, Ct))
        {
            updates.Add(update);
        }

        Assert.Equal("Привіт, світ", string.Concat(updates.Select(u => u.Text)));
        var reasoning = updates.SelectMany(u => u.Contents).OfType<TextReasoningContent>().Select(r => r.Text);
        Assert.Equal("thinking", string.Concat(reasoning));
        Assert.DoesNotContain(updates, u => u.Text.Contains("AFTER", StringComparison.Ordinal));
        Assert.Contains(updates, u => u.FinishReason == ChatFinishReason.Stop);
        var usage = updates.SelectMany(u => u.Contents).OfType<UsageContent>().Single();
        Assert.Equal(5, usage.Details.TotalTokenCount);
        Assert.All(updates, u => Assert.Equal(ChatRole.Assistant, u.Role));

        var json = Assert.Single(server.Requests).Json;
        Assert.True(json.GetProperty("stream").GetBoolean());
        Assert.True(json.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(8, json.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task GetStreamingResponse_StopsAtEndOfStreamWithoutDone()
    {
        await using var server = new FakeOpenAiServer((_, response) => FakeOpenAiServer.WriteSseAsync(response,
            FakeOpenAiServer.Split("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"a\"}}]}\n\n")));
        using var client = new LlamaChatClient(server.BaseAddress);

        var text = "";
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], null, Ct))
        {
            text += update.Text;
        }

        Assert.Equal("a", text);
    }

    [Fact]
    public async Task GetResponse_HttpError_ThrowsWithServerMessage()
    {
        await using var server = new FakeOpenAiServer((_, response) => FakeOpenAiServer.WriteJsonAsync(response,
            """{"error":{"code":500,"message":"model crashed","type":"server_error"}}""", status: 500));
        using var client = new LlamaChatClient(server.BaseAddress);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], null, Ct));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Contains("model crashed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStreamingResponse_HttpError_ThrowsWithBody()
    {
        await using var server = new FakeOpenAiServer((_, response) =>
            FakeOpenAiServer.WriteJsonAsync(response, "plain failure text", status: 400));
        using var client = new LlamaChatClient(server.BaseAddress);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], null, Ct))
            {
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("plain failure text", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStreamingResponse_ErrorEvent_Throws()
    {
        await using var server = new FakeOpenAiServer((_, response) => FakeOpenAiServer.WriteSseAsync(response, FakeOpenAiServer.Split(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}}]}\n\n"
            + "data: {\"error\":{\"code\":400,\"message\":\"context size exceeded\",\"type\":\"exceed_context_size_error\"}}\n\n")));
        using var client = new LlamaChatClient(server.BaseAddress);

        var received = new List<string>();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], null, Ct))
            {
                received.Add(update.Text);
            }
        });

        Assert.Equal(["partial"], received);
        Assert.Contains("context size exceeded", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetService_ReturnsMetadataAndSelf()
    {
        using var client = new LlamaChatClient(new Uri("http://127.0.0.1:1/"), modelId: "m");

        var metadata = Assert.IsType<ChatClientMetadata>(client.GetService(typeof(ChatClientMetadata)));
        Assert.Equal("llama.cpp", metadata.ProviderName);
        Assert.Equal("m", metadata.DefaultModelId);
        Assert.Same(client, client.GetService(typeof(LlamaChatClient)));
        Assert.Null(client.GetService(typeof(string)));
        Assert.Null(client.GetService(typeof(ChatClientMetadata), "key"));
    }

    [Fact]
    public void CreateRequest_SerializesWithTheSourceGeneratedContext()
    {
        using var client = new LlamaChatClient(new Uri("http://127.0.0.1:1"));
        var request = client.CreateRequest(
            [new ChatMessage(ChatRole.User, "x")], new ChatOptions { TopK = 40, Seed = 7 }, stream: false);

        var json = JsonSerializer.Serialize(request, OpenAiJsonContext.Default.ChatCompletionRequest);

        Assert.Contains("\"top_k\":40", json, StringComparison.Ordinal);
        Assert.Contains("\"seed\":7", json, StringComparison.Ordinal);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReasoningEffort.None, false)]
    [InlineData(ReasoningEffort.Medium, true)]
    public void CreateRequest_MapsReasoningEffortToTheTemplateThinkingSwitch(ReasoningEffort effort, bool enableThinking)
    {
        using var client = new LlamaChatClient(new Uri("http://127.0.0.1:1"));
        var request = client.CreateRequest(
            [new ChatMessage(ChatRole.User, "x")], new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } }, stream: true);

        var json = JsonSerializer.SerializeToElement(request, OpenAiJsonContext.Default.ChatCompletionRequest);

        Assert.Equal(enableThinking, json.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);
}
