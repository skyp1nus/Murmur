using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Murmur.Intelligence.Llama;

/// <summary>HTTP plumbing shared by the chat and embedding clients.</summary>
internal static class OpenAiHttp
{
    private const int MaxErrorBodyChars = 500;

    public static HttpClient CreateDefaultClient() =>
        new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), UseProxy = false })
        {
            // Generation can take minutes on CPU; callers cancel through their token.
            Timeout = Timeout.InfiniteTimeSpan,
        };

    public static Uri Combine(Uri endpoint, string relative)
    {
        var baseUri = endpoint.AbsoluteUri.EndsWith('/') ? endpoint : new Uri(endpoint.AbsoluteUri + "/");
        return new Uri(baseUri, relative);
    }

    public static HttpRequestMessage CreatePost<T>(Uri uri, T body, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body, typeInfo);
        var content = new ByteArrayContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
    }

    /// <summary>Throws <see cref="HttpRequestException"/> with the server's error message for non-2xx responses.</summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = TryGetErrorMessage(body) ?? Truncate(body);
        throw new HttpRequestException(
            $"llama-server returned {(int)response.StatusCode} ({response.ReasonPhrase}): {message}",
            inner: null,
            response.StatusCode);
    }

    public static string? TryGetErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(body, OpenAiJsonContext.Default.ErrorEnvelope)?.Error?.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string body)
    {
        var builder = new StringBuilder(body.Length > MaxErrorBodyChars ? body[..MaxErrorBodyChars] : body);
        if (body.Length > MaxErrorBodyChars)
        {
            builder.Append('…');
        }

        return builder.ToString();
    }
}
