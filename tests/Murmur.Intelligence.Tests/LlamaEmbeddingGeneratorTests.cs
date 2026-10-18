using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Murmur.Intelligence.Llama;
using Murmur.Intelligence.Tests.Fakes;

namespace Murmur.Intelligence.Tests;

public sealed class LlamaEmbeddingGeneratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers every input "xN" with the vector [N, N, 0] (unnormalized), in reverse index order.</summary>
    private static Task ReverseOrderHandler(RecordedRequest request, HttpListenerResponse response)
    {
        var inputs = request.Json.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var json = new StringBuilder("{\"model\":\"embeddinggemma\",\"object\":\"list\",\"data\":[");
        for (var i = inputs.Length - 1; i >= 0; i--)
        {
            var n = int.Parse(inputs[i].AsSpan(1), CultureInfo.InvariantCulture);
            json.Append(CultureInfo.InvariantCulture, $"{{\"object\":\"embedding\",\"index\":{i},\"embedding\":[{n},{n},0]}}");
            json.Append(i > 0 ? "," : "");
        }

        json.Append(CultureInfo.InvariantCulture, $"],\"usage\":{{\"prompt_tokens\":{inputs.Length * 3},\"total_tokens\":{inputs.Length * 3}}}}}");
        return FakeOpenAiServer.WriteJsonAsync(response, json.ToString());
    }

    [Fact]
    public async Task Generate_SplitsIntoBatchesAndKeepsInputOrder()
    {
        await using var server = new FakeOpenAiServer(ReverseOrderHandler);
        using var generator = new LlamaEmbeddingGenerator(server.BaseAddress, modelId: "embeddinggemma", maxBatchSize: 2);

        var result = await generator.GenerateAsync(["x1", "x2", "x3", "x4", "x5"], options: null, Ct);

        Assert.Equal(5, result.Count);
        var requests = server.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.All(requests, r => Assert.Equal("/v1/embeddings", r.Path));
        var batches = requests
            .Select(r => r.Json.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray())
            .OrderBy(b => b[0], StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["x1", "x2"], batches[0]);
        Assert.Equal(["x3", "x4"], batches[1]);
        Assert.Equal(["x5"], batches[2]);
        Assert.All(requests, r => Assert.Equal("float", r.Json.GetProperty("encoding_format").GetString()));
        Assert.All(requests, r => Assert.Equal("embeddinggemma", r.Json.GetProperty("model").GetString()));

        // Every vector is [n, n, 0] normalized to [1/√2, 1/√2, 0]; the next test checks the order.
        foreach (var embedding in result)
        {
            Assert.Equal(1.0, Norm(embedding.Vector.Span), 5);
            Assert.Equal(1 / Math.Sqrt(2), embedding.Vector.Span[0], 5);
            Assert.Equal("embeddinggemma", embedding.ModelId);
        }

        Assert.Equal(15, result.Usage?.InputTokenCount);
    }

    [Fact]
    public async Task Generate_MapsResponseIndexesBackToInputs()
    {
        // Distinguishable vectors: input "xN" -> one-hot at position N.
        await using var server = new FakeOpenAiServer((request, response) =>
        {
            var inputs = request.Json.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray();
            var data = Enumerable.Range(0, inputs.Length).Reverse().Select(i =>
            {
                var n = int.Parse(inputs[i].AsSpan(1), CultureInfo.InvariantCulture);
                var vector = string.Join(",", Enumerable.Range(0, 4).Select(p => p == n ? "5" : "0"));
                return $"{{\"index\":{i},\"embedding\":[{vector}]}}";
            });
            return FakeOpenAiServer.WriteJsonAsync(response, "{\"data\":[" + string.Join(",", data) + "]}");
        });
        using var generator = new LlamaEmbeddingGenerator(server.BaseAddress, maxBatchSize: 3);

        var result = await generator.GenerateAsync(["x2", "x0", "x3", "x1"], options: null, Ct);

        AssertVector([0f, 0f, 1f, 0f], result[0].Vector.ToArray());
        AssertVector([1f, 0f, 0f, 0f], result[1].Vector.ToArray());
        AssertVector([0f, 0f, 0f, 1f], result[2].Vector.ToArray());
        AssertVector([0f, 1f, 0f, 0f], result[3].Vector.ToArray());
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task Generate_EmptyInput_SendsNothing()
    {
        await using var server = new FakeOpenAiServer(ReverseOrderHandler);
        using var generator = new LlamaEmbeddingGenerator(server.BaseAddress);

        var result = await generator.GenerateAsync([], options: null, Ct);

        Assert.Empty(result);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Generate_Dimensions_TruncatesBeforeNormalizing()
    {
        await using var server = new FakeOpenAiServer((_, response) =>
            FakeOpenAiServer.WriteJsonAsync(response, """{"data":[{"index":0,"embedding":[3,4,12]}]}"""));
        using var generator = new LlamaEmbeddingGenerator(server.BaseAddress);

        var result = await generator.GenerateAsync(["a"], new EmbeddingGenerationOptions { Dimensions = 2 }, Ct);

        AssertVector([0.6f, 0.8f], result[0].Vector.ToArray());
    }

    [Fact]
    public async Task Generate_HttpError_Throws()
    {
        await using var server = new FakeOpenAiServer((_, response) => FakeOpenAiServer.WriteJsonAsync(response,
            """{"error":{"code":501,"message":"This server does not support embeddings. Start it with `--embeddings`","type":"not_supported_error"}}""",
            status: 501));
        using var generator = new LlamaEmbeddingGenerator(server.BaseAddress);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => generator.GenerateAsync(["a"], null, Ct));

        Assert.Equal(HttpStatusCode.NotImplemented, ex.StatusCode);
        Assert.Contains("--embeddings", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_CountMismatch_Throws()
    {
        await using var server = new FakeOpenAiServer((_, response) =>
            FakeOpenAiServer.WriteJsonAsync(response, """{"data":[{"index":0,"embedding":[1]}]}"""));
        using var generator = new LlamaEmbeddingGenerator(server.BaseAddress);

        await Assert.ThrowsAsync<InvalidDataException>(() => generator.GenerateAsync(["a", "b"], null, Ct));
    }

    [Fact]
    public void Normalize_ScalesToUnitLengthAndKeepsZeroVectors()
    {
        AssertVector([0.6f, 0.8f], LlamaEmbeddingGenerator.Normalize([3f, 4f]));
        AssertVector([0f, 0f, 0f], LlamaEmbeddingGenerator.Normalize([0f, 0f, 0f]));
        AssertVector([1f], LlamaEmbeddingGenerator.Normalize([7f]));
        AssertVector([0.6f, 0.8f], LlamaEmbeddingGenerator.Normalize([3f, 4f, 100f], dimensions: 2));
        Assert.Equal(3, LlamaEmbeddingGenerator.Normalize([1f, 1f, 1f], dimensions: 10).Length);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveBatchSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlamaEmbeddingGenerator(new Uri("http://127.0.0.1:1/"), maxBatchSize: 0));
    }

    private static void AssertVector(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], actual[i], 5);
        }
    }

    private static double Norm(ReadOnlySpan<float> v)
    {
        double sum = 0;
        foreach (var x in v)
        {
            sum += x * x;
        }

        return Math.Sqrt(sum);
    }
}
