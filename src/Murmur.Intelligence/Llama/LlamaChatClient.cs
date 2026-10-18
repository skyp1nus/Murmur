using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Murmur.Intelligence.Llama;

/// <summary>
/// <see cref="IChatClient"/> over llama-server's OpenAI-compatible <c>POST /v1/chat/completions</c>, with
/// non-streaming and server-sent-events streaming responses.
/// </summary>
public sealed class LlamaChatClient : IChatClient
{
    private const string DataPrefix = "data:";
    private const string DoneMarker = "[DONE]";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _completionsUri;
    private readonly ChatClientMetadata _metadata;

    /// <param name="endpoint">Server base address, e.g. <c>http://127.0.0.1:8080/</c>.</param>
    /// <param name="httpClient">Shared client; when <c>null</c> the instance creates and owns one.</param>
    /// <param name="modelId">Sent as <c>model</c>; llama-server serves its single loaded model regardless.</param>
    public LlamaChatClient(Uri endpoint, HttpClient? httpClient = null, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _http = httpClient ?? OpenAiHttp.CreateDefaultClient();
        _ownsHttp = httpClient is null;
        _completionsUri = OpenAiHttp.Combine(endpoint, "v1/chat/completions");
        _metadata = new ChatClientMetadata("llama.cpp", endpoint, modelId);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = CreateRequest(messages, options, stream: false);
        using var httpRequest = OpenAiHttp.CreatePost(_completionsUri, request, OpenAiJsonContext.Default.ChatCompletionRequest);
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await OpenAiHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var dto = await JsonSerializer.DeserializeAsync(stream, OpenAiJsonContext.Default.ChatCompletionResponse, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("llama-server returned an empty chat response.");
            ThrowIfError(dto);

            var choice = dto.Choices is { Count: > 0 } ? dto.Choices[0] : null;
            var message = new ChatMessage(ChatRole.Assistant, choice?.Message?.Content ?? string.Empty)
            {
                MessageId = dto.Id,
            };
            return new ChatResponse(message)
            {
                ResponseId = dto.Id,
                ModelId = dto.Model,
                CreatedAt = ToTimestamp(dto.Created),
                FinishReason = ToFinishReason(choice?.FinishReason),
                Usage = ToUsage(dto.Usage),
            };
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = CreateRequest(messages, options, stream: true);
        using var httpRequest = OpenAiHttp.CreatePost(_completionsUri, request, OpenAiJsonContext.Default.ChatCompletionRequest);
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await OpenAiHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
                {
                    // Blank event separators, ": keep-alive" comments and other SSE fields.
                    continue;
                }

                var payload = line.AsSpan(DataPrefix.Length).Trim();
                if (payload.IsEmpty)
                {
                    continue;
                }

                if (payload.SequenceEqual(DoneMarker))
                {
                    yield break;
                }

                var chunk = JsonSerializer.Deserialize(payload, OpenAiJsonContext.Default.ChatCompletionResponse);
                if (chunk is null)
                {
                    continue;
                }

                ThrowIfError(chunk);
                if (ToUpdate(chunk) is { } update)
                {
                    yield return update;
                }
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return _metadata;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    internal ChatCompletionRequest CreateRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var request = new ChatCompletionRequest
        {
            Model = options?.ModelId ?? _metadata.DefaultModelId,
            Stream = stream,
            StreamOptions = stream ? new StreamOptionsDto { IncludeUsage = true } : null,
        };

        if (!string.IsNullOrEmpty(options?.Instructions))
        {
            request.Messages.Add(new ChatMessageDto { Role = ChatRole.System.Value, Content = options.Instructions });
        }

        foreach (var message in messages)
        {
            request.Messages.Add(new ChatMessageDto { Role = message.Role.Value, Content = message.Text });
        }

        if (options is not null)
        {
            request.Temperature = options.Temperature;
            request.TopP = options.TopP;
            request.TopK = options.TopK;
            request.MaxTokens = options.MaxOutputTokens;
            request.Seed = options.Seed;
            request.FrequencyPenalty = options.FrequencyPenalty;
            request.PresencePenalty = options.PresencePenalty;
            if (options.StopSequences is { Count: > 0 } stops)
            {
                request.Stop = [.. stops];
            }
        }

        return request;
    }

    private static ChatResponseUpdate? ToUpdate(ChatCompletionResponse chunk)
    {
        var choice = chunk.Choices is { Count: > 0 } ? chunk.Choices[0] : null;
        var text = choice?.Delta?.Content;
        var finish = ToFinishReason(choice?.FinishReason);
        var usage = ToUsage(chunk.Usage);
        if (string.IsNullOrEmpty(text) && finish is null && usage is null)
        {
            // Role-only or reasoning-only delta.
            return null;
        }

        var contents = new List<AIContent>(2);
        if (!string.IsNullOrEmpty(text))
        {
            contents.Add(new TextContent(text));
        }

        if (usage is not null)
        {
            contents.Add(new UsageContent(usage));
        }

        return new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            ResponseId = chunk.Id,
            MessageId = chunk.Id,
            ModelId = chunk.Model,
            CreatedAt = ToTimestamp(chunk.Created),
            FinishReason = finish,
        };
    }

    private static void ThrowIfError(ChatCompletionResponse dto)
    {
        if (dto.Error is { } error)
        {
            throw new HttpRequestException($"llama-server error: {error.Message ?? error.Type ?? "unknown error"}");
        }
    }

    private static DateTimeOffset? ToTimestamp(long? unixSeconds) =>
        unixSeconds is { } s and > 0 ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    private static ChatFinishReason? ToFinishReason(string? reason) => reason switch
    {
        null or "" => null,
        "stop" => ChatFinishReason.Stop,
        "length" => ChatFinishReason.Length,
        "tool_calls" => ChatFinishReason.ToolCalls,
        "content_filter" => ChatFinishReason.ContentFilter,
        _ => new ChatFinishReason(reason),
    };

    private static UsageDetails? ToUsage(UsageDto? usage) => usage is null
        ? null
        : new UsageDetails
        {
            InputTokenCount = usage.PromptTokens,
            OutputTokenCount = usage.CompletionTokens,
            TotalTokenCount = usage.TotalTokens,
        };
}
