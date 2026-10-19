using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Murmur.Core.Models;
using Murmur.Models;

namespace Murmur.ModelFetch;

/// <summary>Where the upstream metadata comes from (the download hosts are <see cref="ModelCatalogSources"/>).</summary>
/// <param name="HuggingFaceApi">Hugging Face, without trailing slash; the API lives under <c>/api</c>.</param>
/// <param name="GitHubApi">GitHub REST API, without trailing slash.</param>
internal sealed record PinEndpoints(string HuggingFaceApi, string GitHubApi)
{
    public static PinEndpoints Default { get; } = new("https://huggingface.co", "https://api.github.com");
}

/// <summary>
/// Asks Hugging Face and GitHub what every source of the catalog is today: the commit of a Hugging Face repository and
/// the SHA-256 and size of the file in it (LFS metadata; small files that are not in LFS are downloaded and hashed), the
/// digest and size of a release asset. Compares that with what the catalog pins so drift is visible.
/// </summary>
internal sealed class PinResolver(HttpClient http, ModelCatalogSources sources, PinEndpoints endpoints, string? gitHubToken, string? huggingFaceToken)
{
    /// <summary>A file that is not in LFS has no SHA-256 in the API; up to this size it is downloaded and hashed instead.</summary>
    private const long MaxHashedDownload = 16 * 1024 * 1024;

    private sealed record Parsed(bool HuggingFace, string Repo, string CatalogRevision, string File);

    private sealed record Entry(PinRow Row, ModelMirror Mirror, Parsed? Parsed);

    public async Task<IReadOnlyList<PinRow>> ResolveAsync(IReadOnlyList<ModelCatalogItem> items, CancellationToken cancellationToken)
    {
        var entries = new List<Entry>();
        foreach (var item in items)
        {
            foreach (var file in item.Files)
            {
                foreach (var mirror in file.Sources)
                {
                    var parsed = Parse(mirror.Url);
                    var row = new PinRow
                    {
                        Item = item.Id,
                        Source = parsed?.Repo ?? mirror.Url,
                        Host = parsed is null ? "?" : parsed.HuggingFace ? "huggingface" : "github",
                        File = parsed?.File ?? file.RelativePath,
                        IsGguf = item.Info.Kind is ModelKind.Chat or ModelKind.Embedding && item.Files.Count == 1,
                    };
                    if (parsed is null)
                    {
                        Fail(row, "not a Hugging Face or GitHub release URL");
                    }

                    entries.Add(new Entry(row, mirror, parsed));
                }
            }
        }

        foreach (var group in entries.Where(e => e.Parsed is { HuggingFace: true }).GroupBy(e => e.Parsed!.Repo, StringComparer.Ordinal))
        {
            await ResolveHuggingFaceAsync(group.Key, [.. group], cancellationToken).ConfigureAwait(false);
        }

        foreach (var group in entries.Where(e => e.Parsed is { HuggingFace: false }).GroupBy(e => (e.Parsed!.Repo, Tag: e.Parsed.CatalogRevision)))
        {
            await ResolveReleaseAsync(group.Key.Repo, group.Key.Tag, [.. group], cancellationToken).ConfigureAwait(false);
        }

        foreach (var entry in entries.Where(e => e.Parsed is not null && e.Row.Status != "ERROR"))
        {
            Judge(entry);
        }

        return [.. entries.Select(e => e.Row)];
    }

    /// <summary>Splits a catalog URL into repository, revision (or release tag) and file.</summary>
    private Parsed? Parse(string url)
    {
        if (url.StartsWith(sources.HuggingFace + "/", StringComparison.Ordinal))
        {
            var rest = url[(sources.HuggingFace.Length + 1)..];
            var marker = rest.IndexOf("/resolve/", StringComparison.Ordinal);
            if (marker > 0)
            {
                var after = rest[(marker + "/resolve/".Length)..];
                var slash = after.IndexOf('/', StringComparison.Ordinal);
                if (slash > 0 && slash < after.Length - 1)
                {
                    return new Parsed(true, rest[..marker], after[..slash], after[(slash + 1)..]);
                }
            }
        }
        else if (url.StartsWith(sources.GitHub + "/", StringComparison.Ordinal))
        {
            var parts = url[(sources.GitHub.Length + 1)..].Split('/');
            if (parts.Length >= 6 && parts[2] == "releases" && parts[3] == "download")
            {
                return new Parsed(false, $"{parts[0]}/{parts[1]}", parts[4], string.Join('/', parts[5..]));
            }
        }

        return null;
    }

    private async Task ResolveHuggingFaceAsync(string repo, List<Entry> entries, CancellationToken cancellationToken)
    {
        try
        {
            var head = (await GetJsonAsync($"{endpoints.HuggingFaceApi}/api/models/{repo}/revision/main", PinJsonContext.Default.HfRevisionResponse, huggingFaceToken, cancellationToken).ConfigureAwait(false))?.Sha;
            if (string.IsNullOrEmpty(head))
            {
                FailAll(entries, "the revision answer has no sha");
                return;
            }

            var request = new HfPathsRequest { Paths = [.. entries.Select(e => e.Parsed!.File).Distinct(StringComparer.Ordinal)] };
            var infos = await PostJsonAsync($"{endpoints.HuggingFaceApi}/api/models/{repo}/paths-info/{head}", request, PinJsonContext.Default.HfPathsRequest, PinJsonContext.Default.ListHfPathInfo, huggingFaceToken, cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                var row = entry.Row;
                row.Revision = head;
                var info = infos?.FirstOrDefault(i => string.Equals(i.Path, entry.Parsed!.File, StringComparison.Ordinal) && i.Type is null or "file");
                if (info is null)
                {
                    Fail(row, $"{entry.Parsed!.File} does not exist at {head}");
                    continue;
                }

                if (info.Lfs is { Oid.Length: > 0 } lfs)
                {
                    row.Sha256 = Normalize(lfs.Oid);
                    row.Size = lfs.Size ?? info.Size;
                }
                else
                {
                    row.Size = info.Size;
                    if (info.Size is { } size and <= MaxHashedDownload)
                    {
                        row.Sha256 = await HashDownloadAsync($"{sources.HuggingFace}/{repo}/resolve/{head}/{EscapePath(entry.Parsed!.File)}", huggingFaceToken, cancellationToken).ConfigureAwait(false);
                        row.Note = "not in LFS: hashed from a download";
                    }
                    else
                    {
                        row.Note = "not in LFS and too big to hash here: sha256 unknown";
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            FailAll(entries, ex.Message);
        }
    }

    private async Task ResolveReleaseAsync(string repo, string tag, List<Entry> entries, CancellationToken cancellationToken)
    {
        try
        {
            var release = await GetJsonAsync($"{endpoints.GitHubApi}/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}", PinJsonContext.Default.GitHubRelease, gitHubToken, cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                var row = entry.Row;
                row.Revision = tag;
                var asset = release?.Assets?.FirstOrDefault(a => string.Equals(a.Name, entry.Parsed!.File, StringComparison.Ordinal));
                if (asset is null)
                {
                    Fail(row, $"release {tag} has no asset {entry.Parsed!.File}");
                    continue;
                }

                row.Size = asset.Size;
                if (asset.Digest is { Length: > 0 } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    row.Sha256 = Normalize(digest);
                }
                else
                {
                    row.Note = "the asset has no sha256 digest";
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            FailAll(entries, ex.Message);
        }
    }

    /// <summary>Compares what the catalog pins for the source with what upstream says now.</summary>
    private static void Judge(Entry entry)
    {
        var (row, mirror, parsed) = (entry.Row, entry.Mirror, entry.Parsed!);
        var notes = new List<string>();
        if (row.Note is not null)
        {
            notes.Add(row.Note);
        }

        if (parsed.HuggingFace)
        {
            if (parsed.CatalogRevision == "main")
            {
                notes.Add("the catalog downloads from main: add the revision");
            }
            else if (!string.Equals(parsed.CatalogRevision, row.Revision, StringComparison.Ordinal))
            {
                notes.Add($"the catalog revision {parsed.CatalogRevision} is behind head");
            }
        }

        if (mirror.Sha256 is null && mirror.SizeBytes is null)
        {
            row.Status = "unpinned";
        }
        else
        {
            var differences = new List<string>();
            if (mirror.SizeBytes is { } pinnedSize && row.Size is { } size && pinnedSize != size)
            {
                differences.Add($"size pinned {pinnedSize}, upstream {size}");
            }

            if (mirror.Sha256 is { } pinnedHash)
            {
                if (row.Sha256 is null)
                {
                    notes.Add("sha256 is not available upstream: only the size was compared");
                }
                else if (!string.Equals(pinnedHash, row.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    differences.Add($"sha256 pinned {pinnedHash}, upstream {row.Sha256}");
                }
            }

            row.Status = differences.Count == 0 ? "match" : "DRIFT";
            notes.AddRange(differences);
        }

        row.Note = notes.Count == 0 ? null : string.Join("; ", notes);
    }

    private static void Fail(PinRow row, string reason)
    {
        row.Status = "ERROR";
        row.Note = reason;
        row.Size = null;
        row.Sha256 = null;
    }

    private static void FailAll(List<Entry> entries, string reason)
    {
        foreach (var entry in entries)
        {
            Fail(entry.Row, reason);
        }
    }

    private static string Normalize(string digest) =>
        (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..] : digest).ToLowerInvariant();

    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private async Task<T?> GetJsonAsync<T>(string url, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendJsonAsync(request, typeInfo, token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse?> PostJsonAsync<TRequest, TResponse>(
        string url,
        TRequest body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> requestInfo,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseInfo,
        string? token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, requestInfo), Encoding.UTF8, "application/json"),
        };
        return await SendJsonAsync(request, responseInfo, token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> SendJsonAsync<T>(HttpRequestMessage request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, string? token, CancellationToken cancellationToken)
    {
        Prepare(request, token, json: true);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{request.Method} {request.RequestUri}: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> HashDownloadAsync(string url, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Prepare(request, token, json: false);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GET {url}: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void Prepare(HttpRequestMessage request, string? token, bool json)
    {
        if (json)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Murmur-ModelFetch", "1.0"));
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}
