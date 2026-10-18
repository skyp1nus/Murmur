using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Murmur.Intelligence.Llama;

/// <summary>
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> over llama-server's <c>POST /v1/embeddings</c>. Inputs are
/// sent in batches of <see cref="MaxBatchSize"/>; every returned vector is L2-normalized, so a dot product is the
/// cosine similarity.
/// </summary>
public sealed class LlamaEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _embeddingsUri;
    private readonly EmbeddingGeneratorMetadata _metadata;

    /// <param name="endpoint">Server base address, e.g. <c>http://127.0.0.1:8081/</c>.</param>
    /// <param name="httpClient">Shared client; when <c>null</c> the instance creates and owns one.</param>
    /// <param name="modelId">Sent as <c>model</c>.</param>
    /// <param name="maxBatchSize">Inputs per HTTP request.</param>
    public LlamaEmbeddingGenerator(Uri endpoint, HttpClient? httpClient = null, string? modelId = null, int maxBatchSize = 16)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchSize, 1);
        _http = httpClient ?? OpenAiHttp.CreateDefaultClient();
        _ownsHttp = httpClient is null;
        _embeddingsUri = OpenAiHttp.Combine(endpoint, "v1/embeddings");
        _metadata = new EmbeddingGeneratorMetadata("llama.cpp", endpoint, modelId);
        MaxBatchSize = maxBatchSize;
    }

    public int MaxBatchSize { get; }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var inputs = values as IReadOnlyList<string> ?? [.. values];
        var result = new GeneratedEmbeddings<Embedding<float>>(inputs.Count);
        long inputTokens = 0;
        var hasUsage = false;

        for (var offset = 0; offset < inputs.Count; offset += MaxBatchSize)
        {
            var count = Math.Min(MaxBatchSize, inputs.Count - offset);
            var request = new EmbeddingRequest { Model = options?.ModelId ?? _metadata.DefaultModelId };
            for (var i = 0; i < count; i++)
            {
                request.Input.Add(inputs[offset + i]);
            }

            var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            var data = response.Data ?? [];
            if (data.Count != count)
            {
                throw new InvalidDataException($"llama-server returned {data.Count} embeddings for {count} inputs.");
            }

            var ordered = new float[]?[count];
            foreach (var item in data)
            {
                if ((uint)item.Index >= (uint)count || ordered[item.Index] is not null)
                {
                    throw new InvalidDataException($"llama-server returned an unexpected embedding index {item.Index}.");
                }

                ordered[item.Index] = item.Embedding ?? throw new InvalidDataException("llama-server returned an empty embedding.");
            }

            var createdAt = DateTimeOffset.UtcNow;
            foreach (var vector in ordered)
            {
                var normalized = Normalize(vector!, options?.Dimensions);
                result.Add(new Embedding<float>(normalized) { ModelId = response.Model, CreatedAt = createdAt });
            }

            if (response.Usage?.PromptTokens is { } tokens)
            {
                inputTokens += tokens;
                hasUsage = true;
            }
        }

        if (hasUsage)
        {
            result.Usage = new UsageDetails { InputTokenCount = inputTokens, TotalTokenCount = inputTokens };
        }

        return result;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
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

    /// <summary>
    /// Truncates to <paramref name="dimensions"/> (Matryoshka models such as EmbeddingGemma allow it) and scales to
    /// unit length in place. A zero vector stays zero.
    /// </summary>
    internal static float[] Normalize(float[] vector, int? dimensions = null)
    {
        if (dimensions is { } d && d > 0 && d < vector.Length)
        {
            vector = vector[..d];
        }

        double sum = 0;
        foreach (var x in vector)
        {
            sum += (double)x * x;
        }

        if (sum > 0)
        {
            var scale = (float)(1.0 / Math.Sqrt(sum));
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] *= scale;
            }
        }

        return vector;
    }

    private async Task<EmbeddingResponse> SendAsync(EmbeddingRequest request, CancellationToken cancellationToken)
    {
        using var httpRequest = OpenAiHttp.CreatePost(_embeddingsUri, request, OpenAiJsonContext.Default.EmbeddingRequest);
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await OpenAiHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync(stream, OpenAiJsonContext.Default.EmbeddingResponse, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("llama-server returned an empty embedding response.");
        }
    }
}
