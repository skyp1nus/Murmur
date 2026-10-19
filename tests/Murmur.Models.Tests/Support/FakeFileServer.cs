using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Murmur.Models.Tests.Support;

internal sealed record RecordedRequest(string Method, string Path, string? Range);

/// <summary>What a path serves and how it misbehaves. Hit numbers start at 1 and count every request to the path.</summary>
internal sealed class Route(byte[] content)
{
    private int _hits;
    private long _bytesServed;

    public byte[] Content { get; set; } = content;

    public bool SupportsRange { get; set; } = true;

    public bool NotFound { get; set; }

    /// <summary>Status to answer with instead of the content (e.g. 500) for the given hit number, or <c>null</c>.</summary>
    public Func<int, int?>? StatusFor { get; set; }

    /// <summary>Never answer this hit (until the server is disposed).</summary>
    public Func<int, bool>? Hang { get; set; }

    /// <summary>Drop the connection after this many body bytes of the response, for the given hit number.</summary>
    public Func<int, long?>? DisconnectAfter { get; set; }

    public int ChunkSize { get; set; } = 32 * 1024;

    public TimeSpan ChunkDelay { get; set; }

    /// <summary>Awaited before the answer of a hit (used to line requests up).</summary>
    public Func<int, Task>? Before { get; set; }

    public int Hits => Volatile.Read(ref _hits);

    public long BytesServed => Interlocked.Read(ref _bytesServed);

    internal int NextHit() => Interlocked.Increment(ref _hits);

    internal void AddServed(long count) => Interlocked.Add(ref _bytesServed, count);
}

/// <summary>HttpListener on 127.0.0.1 serving <see cref="Route"/>s with Range support, as the real hosts do.</summary>
internal sealed class FakeFileServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loop;
    private readonly CancellationTokenSource _stop = new();
    private int _active;
    private int _maxActive;

    public FakeFileServer()
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                BaseAddress = $"http://127.0.0.1:{port}";
                break;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                listener.Close();
            }
        }

        _loop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Without trailing slash.</summary>
    public string BaseAddress { get; }

    public ConcurrentDictionary<string, Route> Routes { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public int MaxConcurrentRequests => Volatile.Read(ref _maxActive);

    public string Url(string path) => BaseAddress + path;

    public Route Add(string path, byte[] content)
    {
        var route = new Route(content);
        Routes[path] = route;
        return route;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
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
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException or ArgumentException or TimeoutException)
        {
        }

        _stop.Dispose();
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
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException or ArgumentException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var current = Interlocked.Increment(ref _active);
        int seen;
        while (current > (seen = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, current, seen) != seen)
        {
        }

        try
        {
            await ServeAsync(context);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException or OperationCanceledException or InvalidOperationException)
        {
            // Client went away or the server is shutting down.
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url!.AbsolutePath;
        var range = request.Headers["Range"];
        Requests.Enqueue(new RecordedRequest(request.HttpMethod, path, range));

        if (!Routes.TryGetValue(path, out var route) || route.NotFound)
        {
            response.StatusCode = 404;
            response.Close();
            return;
        }

        var hit = route.NextHit();
        if (route.Before is { } before)
        {
            await before(hit);
        }

        if (route.Hang?.Invoke(hit) == true)
        {
            await Task.Delay(Timeout.Infinite, _stop.Token);
        }

        if (route.StatusFor?.Invoke(hit) is { } status)
        {
            response.StatusCode = status;
            response.Close();
            return;
        }

        var content = route.Content;
        long start = 0;
        long count = content.Length;
        if (route.SupportsRange && TryParseRange(range, content.Length, out var from, out var to))
        {
            if (from >= content.Length)
            {
                response.StatusCode = 416;
                response.AddHeader("Content-Range", $"bytes */{content.Length}");
                response.Close();
                return;
            }

            start = from;
            count = to - from + 1;
            response.StatusCode = 206;
            response.AddHeader("Content-Range", $"bytes {from}-{to}/{content.Length}");
        }
        else
        {
            response.StatusCode = 200;
        }

        response.ContentLength64 = count;
        var cutoff = route.DisconnectAfter?.Invoke(hit);
        long sent = 0;
        while (sent < count)
        {
            var size = (int)Math.Min(route.ChunkSize, count - sent);
            if (cutoff is { } limit)
            {
                size = (int)Math.Min(size, Math.Max(0, limit - sent));
                if (size == 0)
                {
                    await response.OutputStream.FlushAsync();
                    response.Abort();
                    return;
                }
            }

            await response.OutputStream.WriteAsync(content.AsMemory((int)(start + sent), size));
            await response.OutputStream.FlushAsync();
            sent += size;
            if (range != "bytes=0-0")
            {
                route.AddServed(size); // size probes do not count as transfer
            }

            if (route.ChunkDelay > TimeSpan.Zero)
            {
                await Task.Delay(route.ChunkDelay, _stop.Token);
            }
        }

        response.Close();
    }

    private static bool TryParseRange(string? header, long length, out long from, out long to)
    {
        from = 0;
        to = length - 1;
        if (header is null || !header.StartsWith("bytes=", StringComparison.Ordinal))
        {
            return false;
        }

        var spec = header["bytes=".Length..].Split('-');
        if (spec.Length != 2 || !long.TryParse(spec[0], out from))
        {
            return false;
        }

        to = long.TryParse(spec[1], out var end) ? Math.Min(end, length - 1) : length - 1;
        return true;
    }
}
