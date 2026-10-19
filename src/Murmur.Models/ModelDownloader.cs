using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace Murmur.Models;

/// <summary>
/// Streams one URL into a <c>.part</c> file: resumes with a <c>Range</c> request, restarts when the server ignores it,
/// retries transient failures with backoff. Verification (hash) and the final rename are the caller's job.
/// </summary>
internal sealed class ModelDownloader(HttpClient http, IReadOnlyList<TimeSpan> retryDelays, TimeSpan stallTimeout)
{
    private const int BufferSize = 128 * 1024;

    /// <summary>The size the server reports for the URL (<c>Range: bytes=0-0</c> request), or -1 when it does not say.</summary>
    public Task<long> ProbeAsync(string url, CancellationToken cancellationToken) =>
        RetryAsync($"Probing {url}", () => ProbeOnceAsync(url, cancellationToken), () => 0, cancellationToken);

    /// <summary>
    /// Downloads until the part file holds the whole body. <paramref name="knownTotal"/> is the size from the probe
    /// (-1 when unknown); <paramref name="onProgress"/> gets the part file length (also after a restart, then 0);
    /// <paramref name="onTotal"/> the size announced by the response.
    /// </summary>
    public Task DownloadAsync(string url, string partPath, long knownTotal, Action<long> onProgress, Action<long> onTotal, CancellationToken cancellationToken) =>
        RetryAsync(
            $"Downloading {url}",
            async () =>
            {
                await DownloadOnceAsync(url, partPath, knownTotal, onProgress, onTotal, cancellationToken).ConfigureAwait(false);
                return 0;
            },
            () => File.Exists(partPath) ? new FileInfo(partPath).Length : 0,
            cancellationToken);

    private async Task<long> ProbeOnceAsync(string url, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(stallTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(0, 0) } };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
        ThrowForStatus(response, url);
        if (response.StatusCode is HttpStatusCode.PartialContent or HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return response.Content.Headers.ContentRange?.Length ?? -1;
        }

        // 200: the server ignored the range; the body is not read, disposing the response drops the connection.
        return response.Content.Headers.ContentLength ?? -1;
    }

    private async Task DownloadOnceAsync(string url, string partPath, long knownTotal, Action<long> onProgress, Action<long> onTotal, CancellationToken cancellationToken)
    {
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (knownTotal >= 0)
        {
            if (existing == knownTotal)
            {
                onProgress(existing);
                return;
            }

            if (existing > knownTotal)
            {
                File.Delete(partPath);
                existing = 0;
            }
        }

        onProgress(existing);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(stallTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
        ThrowForStatus(response, url, allowRangeNotSatisfiable: existing > 0);

        long total;
        bool append;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (existing == 0 || range?.From != existing)
            {
                // Not the bytes we asked for: start over on the next attempt.
                File.Delete(partPath);
                throw new IOException($"Unexpected Content-Range from {url}");
            }

            total = range.Length ?? (response.Content.Headers.ContentLength is { } rest ? existing + rest : -1);
            append = true;
        }
        else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var announced = response.Content.Headers.ContentRange?.Length;
            if (announced == existing)
            {
                onTotal(existing);
                return;
            }

            File.Delete(partPath);
            throw new IOException($"Range not satisfiable for {url}; restarting");
        }
        else
        {
            // 200: no range support (or a fresh start). The whole body follows.
            total = response.Content.Headers.ContentLength ?? -1;
            append = false;
            existing = 0;
        }

        if (total >= 0)
        {
            onTotal(total);
        }

        var position = existing;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
            await using var file = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);
            if (!append)
            {
                file.SetLength(0);
                onProgress(0);
            }

            file.Seek(0, SeekOrigin.End);
            await using var body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            while (true)
            {
                stall.CancelAfter(stallTimeout);
                var read = await body.ReadAsync(buffer.AsMemory(0, BufferSize), stall.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                try
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    throw new ModelInstallException($"Writing {partPath} failed: {ex.Message}", ex);
                }

                position += read;
                onProgress(position);
            }

            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (total >= 0 && position < total)
        {
            throw new IOException($"Connection closed after {position} of {total} bytes: {url}");
        }

        if (total >= 0 && position > total)
        {
            File.Delete(partPath);
            throw new ModelInstallException($"{url} delivered {position} bytes, more than the announced {total}");
        }
    }

    private static void ThrowForStatus(HttpResponseMessage response, string url, bool allowRangeNotSatisfiable = true)
    {
        var status = response.StatusCode;
        if (status is HttpStatusCode.OK or HttpStatusCode.PartialContent)
        {
            return;
        }

        if (status == HttpStatusCode.RequestedRangeNotSatisfiable && allowRangeNotSatisfiable)
        {
            return;
        }

        if ((int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException($"HTTP {(int)status} from {url}", null, status);
        }

        throw new ModelInstallException($"HTTP {(int)status} {response.ReasonPhrase} from {url}");
    }

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) =>
        ex switch
        {
            ModelInstallException => false,
            OperationCanceledException => !cancellationToken.IsCancellationRequested, // the stall timeout, not the caller
            HttpRequestException or IOException or TimeoutException => true,
            _ => false,
        };

    private async Task<T> RetryAsync<T>(string what, Func<Task<T>> action, Func<long> progress, CancellationToken cancellationToken)
    {
        // A failure after progress resets the budget: a flaky link that keeps delivering bytes is not a dead link.
        var failures = 0;
        while (true)
        {
            var before = progress();
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                if (progress() > before)
                {
                    failures = 0;
                }

                if (failures >= retryDelays.Count)
                {
                    var reason = ex is OperationCanceledException ? $"no data for {stallTimeout.TotalSeconds:0.#} s" : ex.Message;
                    throw new ModelInstallException($"{what} failed after {failures} retries: {reason}", ex);
                }

                await Task.Delay(retryDelays[failures++], cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
