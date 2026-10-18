using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Murmur.Intelligence.Tests.Fakes;

internal sealed record RecordedRequest(string Method, string Path, string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
}

/// <summary>Minimal OpenAI-compatible HTTP server on 127.0.0.1 with a random port, driven by a test handler.</summary>
internal sealed class FakeOpenAiServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Func<RecordedRequest, HttpListenerResponse, Task> _handler;
    private readonly Task _loop;

    public FakeOpenAiServer(Func<RecordedRequest, HttpListenerResponse, Task> handler)
    {
        _handler = handler;
        for (var attempt = 0; ; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                BaseAddress = new Uri($"http://127.0.0.1:{port}/");
                break;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                listener.Close();
            }
        }

        _loop = Task.Run(AcceptLoopAsync);
    }

    public Uri BaseAddress { get; }

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public static async Task WriteJsonAsync(HttpListenerResponse response, string json, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// <summary>Writes raw byte pieces with a flush and a short pause between them, as SSE.</summary>
    public static async Task WriteSseAsync(HttpListenerResponse response, IEnumerable<byte[]> pieces)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.SendChunked = true;
        foreach (var piece in pieces)
        {
            await response.OutputStream.WriteAsync(piece);
            await response.OutputStream.FlushAsync();
            await Task.Delay(15);
        }

        response.Close();
    }

    /// <summary>Encodes <paramref name="text"/> as UTF-8 and cuts it at the given byte offsets (may split characters).</summary>
    public static IEnumerable<byte[]> Split(string text, params int[] byteOffsets)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var previous = 0;
        foreach (var offset in byteOffsets.Append(bytes.Length))
        {
            yield return bytes[previous..offset];
            previous = offset;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Stop only: it fails the pending GetContextAsync. On Unix the managed HttpListener re-binds the port when a
        // stopped listener is closed again, which throws "Address already in use" once another test owns the port.
        try
        {
            _listener.Stop();
        }
        catch (HttpListenerException)
        {
        }

        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var request = new RecordedRequest(context.Request.HttpMethod, context.Request.Url!.AbsolutePath, body);
            Requests.Enqueue(request);
            await _handler(request, context.Response);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
        {
            // Client went away.
        }
    }
}
