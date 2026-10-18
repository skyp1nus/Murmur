using System.Text.Json.Serialization;

namespace Murmur.Intelligence.Llama;

// Wire types of the OpenAI-compatible API served by llama-server. Only the fields Murmur uses are modelled.

internal sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("messages")]
    public List<ChatMessageDto> Messages { get; set; } = [];

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("stream_options")]
    public StreamOptionsDto? StreamOptions { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("stop")]
    public List<string>? Stop { get; set; }

    [JsonPropertyName("seed")]
    public long? Seed { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public float? FrequencyPenalty { get; set; }

    [JsonPropertyName("presence_penalty")]
    public float? PresencePenalty { get; set; }
}

internal sealed class StreamOptionsDto
{
    [JsonPropertyName("include_usage")]
    public bool IncludeUsage { get; set; }
}

internal sealed class ChatMessageDto
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>llama-server puts the thinking of reasoning models here.</summary>
    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }
}

internal sealed class ChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("created")]
    public long? Created { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("choices")]
    public List<ChatChoiceDto>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public UsageDto? Usage { get; set; }

    [JsonPropertyName("error")]
    public ErrorDto? Error { get; set; }
}

internal sealed class ChatChoiceDto
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public ChatMessageDto? Message { get; set; }

    [JsonPropertyName("delta")]
    public ChatMessageDto? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

internal sealed class UsageDto
{
    [JsonPropertyName("prompt_tokens")]
    public long? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public long? CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public long? TotalTokens { get; set; }
}

internal sealed class ErrorDto
{
    [JsonPropertyName("code")]
    public int? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

internal sealed class ErrorEnvelope
{
    [JsonPropertyName("error")]
    public ErrorDto? Error { get; set; }
}

internal sealed class EmbeddingRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("input")]
    public List<string> Input { get; set; } = [];

    [JsonPropertyName("encoding_format")]
    public string EncodingFormat { get; set; } = "float";
}

internal sealed class EmbeddingResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("data")]
    public List<EmbeddingDataDto>? Data { get; set; }

    [JsonPropertyName("usage")]
    public UsageDto? Usage { get; set; }
}

internal sealed class EmbeddingDataDto
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ErrorEnvelope))]
[JsonSerializable(typeof(EmbeddingRequest))]
[JsonSerializable(typeof(EmbeddingResponse))]
internal sealed partial class OpenAiJsonContext : JsonSerializerContext;
