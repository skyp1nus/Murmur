using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Murmur.ModelFetch;
using Murmur.Models.Tests.Support;

namespace Murmur.Models.Tests;

public sealed class ResolvePinsTests
{
    private const string Parakeet = "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";
    private const string ParakeetRevision = "2bda32ec70b097a55adaa07d9a7173915b43cc78";
    private const string EmbeddingRevision = "0f741b5a6585bd53aeb15cd1372c56f2a0f65e12";
    private static readonly string ParakeetHead = new('a', 40);
    private static readonly string OtherHead = new('c', 40);
    private static readonly byte[] TokensBody = Encoding.UTF8.GetBytes("<unk> 0\n");

    private static readonly ModelCatalogSources Hosts = new("http://hf.test", "http://gh.test");
    private static readonly PinEndpoints Api = new("http://hf.test", "http://api.gh.test");

    private sealed record Seen(HttpMethod Method, string Url, string? Authorization, string? Body);

    /// <summary>An HTTP handler answering from canned API responses; whatever it does not know is a 404.</summary>
    private sealed class FakeApi(Func<HttpMethod, string, string?, (HttpStatusCode Status, byte[] Body)?> answer) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var url = request.RequestUri!.ToString();
            lock (Requests)
            {
                Requests.Add(new Seen(request.Method, url, request.Headers.Authorization?.ToString(), body));
            }

            var (status, bytes) = answer(request.Method, url, body) ?? (HttpStatusCode.NotFound, "{}"u8.ToArray());
            return new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
        }
    }

    private static (HttpStatusCode, byte[]) Json(string json) => (HttpStatusCode.OK, Encoding.UTF8.GetBytes(json));

    private static string LfsFile(string path, long size, string oid) =>
        "{\"type\":\"file\",\"path\":\"" + path + "\",\"size\":" + size + ",\"oid\":\"git\",\"lfs\":{\"oid\":\"" + oid + "\",\"size\":" + size + ",\"pointerSize\":134}}";

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Every repository exists, has head <c>cccc...</c> and every file is a 1000-byte LFS file; Parakeet and the releases are special.</summary>
    private static (HttpStatusCode, byte[])? Answer(HttpMethod method, string url, string? body)
    {
        const string hf = "http://hf.test/api/models/";
        if (url.StartsWith(hf, StringComparison.Ordinal))
        {
            var rest = url[hf.Length..];
            if (method == HttpMethod.Get && rest.EndsWith("/revision/main", StringComparison.Ordinal))
            {
                return Json($$"""{"sha":"{{(rest.StartsWith(Parakeet, StringComparison.Ordinal) ? ParakeetHead : OtherHead)}}","id":"x"}""");
            }

            if (method == HttpMethod.Post && rest.Contains("/paths-info/", StringComparison.Ordinal))
            {
                var paths = JsonDocument.Parse(body!).RootElement.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!).ToList();
                if (rest.StartsWith(Parakeet, StringComparison.Ordinal))
                {
                    return Json(
                        "[" + string.Join(
                            ",",
                            LfsFile("encoder.int8.onnx", 652184281, "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247"),
                            LfsFile("decoder.int8.onnx", 11845275, new string('d', 64)),
                            LfsFile("joiner.int8.onnx", 6355277, "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3"),
                            "{\"type\":\"file\",\"path\":\"tokens.txt\",\"size\":" + TokensBody.Length + ",\"oid\":\"git4\"}") + "]");
                }

                return Json("[" + string.Join(",", paths.Select(p => LfsFile(p, 1000, Sha(p)))) + "]");
            }

            return null;
        }

        if (url == $"http://hf.test/{Parakeet}/resolve/{ParakeetRevision}/tokens.txt")
        {
            return (HttpStatusCode.OK, TokensBody);
        }

        return url switch
        {
            "http://api.gh.test/repos/k2-fsa/sherpa-onnx/releases/tags/asr-models" =>
                Json("""{"tag_name":"asr-models","assets":[{"name":"silero_vad.onnx","size":643854,"digest":"sha256:9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6"},{"name":"other.bin","size":1}]}"""),
            "http://api.gh.test/repos/ggml-org/llama.cpp/releases/tags/b11716" =>
                Json(
                    """
                    {"assets":[
                      {"name":"llama-b11716-bin-ubuntu-x64.tar.gz","size":17968449,"digest":"sha256:8c08b19bee40d118b803d58fd8f6633ba311ce3c7f1f0138262e636f02aa11f0"},
                      {"name":"llama-b11716-bin-win-cpu-x64.zip","size":19662702,"digest":"sha256:79B143075AB87FB5AFF6F8F8CA80500A984F4120B5199FA9F9B70B284F17428E"}
                    ]}
                    """),
            _ => null,
        };
    }

    private static async Task<(int Code, string Output, string Error, FakeApi Api)> RunAsync(
        ModelCatalogSources? sources = null,
        Func<HttpMethod, string, string?, (HttpStatusCode, byte[])?>? answer = null,
        Dictionary<string, string>? env = null,
        params string[] args)
    {
        var api = new FakeApi(answer ?? Answer);
        using var http = new HttpClient(api);
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await FetchRunner.RunAsync(["--resolve-pins", .. args], output, error, sources ?? Hosts, TestContext.Current.CancellationToken, Api, http, name => env is not null && env.TryGetValue(name, out var value) ? value : null);
        return (code, output.ToString(), error.ToString(), api);
    }

    [Fact]
    public void Parse_ResolvePinsNeedsNoDirectory_ButIdsDo()
    {
        var alone = FetchOptions.Parse(["--resolve-pins"], out var error);
        Assert.Null(error);
        Assert.True(alone!.ResolvePins);
        Assert.Null(alone.Directory);

        Assert.Null(FetchOptions.Parse(["--resolve-pins", "--ids", "a"], out error));
        Assert.NotNull(error);
        Assert.False(FetchOptions.Parse(["--dir", "/m", "--ids", "a"], out _)!.ResolvePins);
    }

    [Fact]
    public async Task ResolvePins_PrintsTheTableTheRevisionsAndTheSnippet()
    {
        var (code, output, error, _) = await RunAsync();

        Assert.Equal(0, code);
        Assert.Equal("", error);
        // Table: item, source, revision, file, size, sha256, status.
        Assert.Contains($"| `silero-vad` | k2-fsa/sherpa-onnx | `asr-models` | `silero_vad.onnx` | 643854 | `9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6` | match |", output);
        Assert.Contains($"| `parakeet-tdt-0.6b-v3-int8` | {Parakeet} | `{ParakeetRevision}` | `encoder.int8.onnx` | 652184281 | `acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247` | match |", output);
        // The Windows runtime is listed too (the digest is lower-cased before it is compared with the pin).
        Assert.Contains("| `llama-cpp-b11716` | ggml-org/llama.cpp | `b11716` | `llama-b11716-bin-win-cpu-x64.zip` | 19662702 | `79b143075ab87fb5aff6f8f8ca80500a984f4120b5199fa9f9b70b284f17428e` | match |", output);
        // Paste-ready: revisions per repository, one line per source.
        Assert.Contains($"[\"{Parakeet}\"] = \"{ParakeetRevision}\",", output);
        Assert.Contains($"[\"ggml-org/embeddinggemma-300M-GGUF\"] = \"{EmbeddingRevision}\",", output);
        Assert.Contains($"new GgufSource(\"ggml-org/embeddinggemma-300M-GGUF\", \"embeddinggemma-300M-Q8_0.gguf\", \"{Sha("embeddinggemma-300M-Q8_0.gguf")}\", 1_000)", output);
        Assert.Contains("\"joiner.int8.onnx\" => (\"3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3\", 6_355_277),", output);
        Assert.Contains("(\"9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6\", 643_854) // silero_vad.onnx", output);
    }

    [Fact]
    public async Task ResolvePins_PrintsJsonThatParses()
    {
        var (_, output, _, _) = await RunAsync();

        var start = output.IndexOf("```json", StringComparison.Ordinal) + "```json".Length;
        var json = JsonDocument.Parse(output[start..output.IndexOf("```", start, StringComparison.Ordinal)]).RootElement;
        var vad = json.EnumerateArray().Single(r => r.GetProperty("item").GetString() == "silero-vad");
        Assert.Equal("github", vad.GetProperty("host").GetString());
        Assert.Equal("asr-models", vad.GetProperty("revision").GetString());
        Assert.Equal(643_854, vad.GetProperty("size").GetInt64());
        Assert.Equal("match", vad.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ResolvePins_DetectsDriftOfHashAndSize_AndWarns()
    {
        var (code, output, _, _) = await RunAsync();

        Assert.Equal(0, code); // drift is reported, it is not a failure of the tool
        Assert.Contains($"| `parakeet-tdt-0.6b-v3-int8` | {Parakeet} | `{ParakeetRevision}` | `decoder.int8.onnx` | 11845275 | `{new string('d', 64)}` | DRIFT |", output);
        Assert.Contains($"sha256 pinned 179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e, upstream {new string('d', 64)}", output);
        Assert.Contains("::warning::pin drift in parakeet-tdt-0.6b-v3-int8", output);
        Assert.Contains("pinned source(s) DRIFTED", output);
        Assert.DoesNotContain("the catalog downloads from main", output); // every repository of the catalog has a revision
    }

    [Fact]
    public async Task ResolvePins_HashesASmallFileThatIsNotInLfs_AndComparesTheSize()
    {
        var (_, output, _, _) = await RunAsync();

        var hash = Convert.ToHexStringLower(SHA256.HashData(TokensBody));
        var row = output.Split('\n').First(l => l.Contains("`tokens.txt`", StringComparison.Ordinal));
        Assert.Contains($"| {TokensBody.Length} | `{hash}` | DRIFT |", row);
        Assert.Contains("not in LFS: hashed from a download", output);
        Assert.Contains($"size pinned 93939, upstream {TokensBody.Length}", output);
    }

    [Fact]
    public async Task ResolvePins_JudgesAPinnedRevisionAtThatRevision_AndReportsTheHeadAsAnUpdate()
    {
        var (_, output, _, api) = await RunAsync();

        // The values are read at the catalog revision; the head of main only produces a note.
        Assert.DoesNotContain(api.Requests, r => r.Url.Contains($"/paths-info/{ParakeetHead}", StringComparison.Ordinal));
        Assert.Contains(api.Requests, r => r.Url == $"http://hf.test/api/models/{Parakeet}/paths-info/{ParakeetRevision}");
        Assert.DoesNotContain(api.Requests, r => r.Url.Contains($"/paths-info/{OtherHead}", StringComparison.Ordinal));
        var encoder = output.Split('\n').First(l => l.Contains("`encoder.int8.onnx`", StringComparison.Ordinal));
        Assert.Contains($"| `{ParakeetRevision}` |", encoder);
        Assert.Contains($"| match | update available: main is at {ParakeetHead} |", encoder);
        Assert.DoesNotContain("downloads from main", encoder);
        Assert.DoesNotContain("behind head", output);
    }

    [Fact]
    public async Task ResolvePins_ARepositoryOnMain_IsReadAtTheHeadWithAHintToAddTheRevision()
    {
        var sources = Hosts with { Revisions = new Dictionary<string, string>() };

        var (_, output, _, api) = await RunAsync(sources, answer: (method, url, body) => url.Contains("/resolve/main/tokens.txt", StringComparison.Ordinal) ? (HttpStatusCode.OK, TokensBody) : Answer(method, url, body));

        Assert.Contains(api.Requests, r => r.Url == $"http://hf.test/api/models/{Parakeet}/paths-info/{ParakeetHead}");
        var encoder = output.Split('\n').First(l => l.Contains("`encoder.int8.onnx`", StringComparison.Ordinal));
        Assert.Contains($"| `{ParakeetHead}` |", encoder);
        Assert.Contains("the catalog downloads from main: add the revision", encoder);
        Assert.DoesNotContain("update available", encoder);
    }

    [Fact]
    public async Task ResolvePins_AsksTheDocumentedApis_AndSendsTokensOnlyToTheirHost()
    {
        var env = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghs_secret", ["HF_TOKEN"] = "hf_secret" };

        var (_, _, _, api) = await RunAsync(env: env);

        Assert.Contains(api.Requests, r => r.Method == HttpMethod.Get && r.Url == $"http://hf.test/api/models/{Parakeet}/revision/main");
        var post = Assert.Single(api.Requests, r => r.Method == HttpMethod.Post && r.Url == $"http://hf.test/api/models/{Parakeet}/paths-info/{ParakeetRevision}");
        Assert.Equal(["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"], JsonDocument.Parse(post.Body!).RootElement.GetProperty("paths").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains(api.Requests, r => r.Url == "http://api.gh.test/repos/ggml-org/llama.cpp/releases/tags/b11716");
        Assert.All(api.Requests.Where(r => r.Url.StartsWith("http://api.gh.test", StringComparison.Ordinal)), r => Assert.Equal("Bearer ghs_secret", r.Authorization));
        Assert.All(api.Requests.Where(r => r.Url.StartsWith("http://hf.test", StringComparison.Ordinal)), r => Assert.Equal("Bearer hf_secret", r.Authorization));
        Assert.Single(api.Requests, r => r.Url.Contains("/revision/main", StringComparison.Ordinal) && r.Url.Contains(Parakeet, StringComparison.Ordinal)); // once per repository
    }

    [Fact]
    public async Task ResolvePins_TheHuggingFaceTokenNeverGoesToAnotherHost_NorIntoTheOutput()
    {
        // Downloads come from a different host than the API: the token is for the API host only.
        var sources = new ModelCatalogSources("http://dl.test", "http://gh.test");
        var env = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghs_secret", ["HF_TOKEN"] = "hf_secret" };
        var summary = Path.Combine(Path.GetTempPath(), "murmur-pins-" + Guid.NewGuid().ToString("N") + ".md");
        env["GITHUB_STEP_SUMMARY"] = summary;
        try
        {
            var (_, output, error, api) = await RunAsync(
                sources,
                (method, url, body) => Answer(method, url.Replace("http://dl.test", "http://hf.test", StringComparison.Ordinal), body),
                env);

            var download = Assert.Single(api.Requests, r => r.Url.EndsWith("/resolve/" + ParakeetRevision + "/tokens.txt", StringComparison.Ordinal));
            Assert.StartsWith("http://dl.test/", download.Url, StringComparison.Ordinal);
            Assert.Null(download.Authorization);
            Assert.All(api.Requests.Where(r => r.Url.StartsWith("http://dl.test", StringComparison.Ordinal)), r => Assert.Null(r.Authorization));
            Assert.Contains("not in LFS: hashed from a download", output);
            var markdown = await File.ReadAllTextAsync(summary, TestContext.Current.CancellationToken);
            foreach (var text in new[] { output, error, markdown })
            {
                Assert.DoesNotContain("hf_secret", text);
                Assert.DoesNotContain("ghs_secret", text);
            }
        }
        finally
        {
            File.Delete(summary);
        }
    }

    [Fact]
    public async Task ResolvePins_TheHuggingFaceTokenGoesToADownloadOnTheApiHost()
    {
        var (_, _, _, api) = await RunAsync(env: new Dictionary<string, string> { ["HF_TOKEN"] = "hf_secret" });

        var download = Assert.Single(api.Requests, r => r.Url.EndsWith("/tokens.txt", StringComparison.Ordinal));
        Assert.Equal("Bearer hf_secret", download.Authorization);
    }

    [Fact]
    public async Task ResolvePins_WithoutTokens_SendsNoAuthorization()
    {
        var (_, _, _, api) = await RunAsync();

        Assert.All(api.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task ResolvePins_FailsForASourceThatCannotBeResolved_ButStillPrintsTheOthers()
    {
        var (code, output, _, _) = await RunAsync(answer: (method, url, body) => url.Contains("unsloth/", StringComparison.Ordinal) ? null : Answer(method, url, body));

        Assert.Equal(1, code);
        Assert.Contains("ERROR", output);
        Assert.Contains("404", output);
        Assert.Contains("could not resolve", output);
        Assert.Contains("| `silero-vad` | k2-fsa/sherpa-onnx |", output);
        Assert.Contains("source(s) could not be resolved", output);
    }

    [Fact]
    public async Task ResolvePins_FailsWhenAnAssetOrAFileIsMissingUpstream()
    {
        var (code, output, _, _) = await RunAsync(answer: (method, url, body) =>
            url.EndsWith("/releases/tags/asr-models", StringComparison.Ordinal) ? Json("""{"assets":[]}""") : Answer(method, url, body));

        Assert.Equal(1, code);
        Assert.Contains("release asr-models has no asset silero_vad.onnx", output);
    }

    [Fact]
    public async Task ResolvePins_ANetworkErrorBecomesAnErrorRow_NotACrash()
    {
        var (code, output, _, _) = await RunAsync(answer: (method, url, body) =>
            url.Contains("/releases/tags/asr-models", StringComparison.Ordinal) ? throw new IOException("connection reset by peer") : Answer(method, url, body));

        Assert.Equal(1, code);
        Assert.Contains("connection reset by peer", output);
        var vad = output.Split('\n').First(l => l.Contains("| `silero-vad` |", StringComparison.Ordinal));
        Assert.Contains("| ERROR |", vad);
        Assert.Contains("| `parakeet-tdt-0.6b-v3-int8` |", output);
        Assert.Contains("| `llama-cpp-b11716` | ggml-org/llama.cpp | `b11716` | `llama-b11716-bin-ubuntu-x64.tar.gz` | 17968449 |", output);
    }

    [Fact]
    public async Task ResolvePins_AHuggingFaceNetworkErrorBecomesAnErrorRow()
    {
        var (code, output, _, _) = await RunAsync(answer: (method, url, body) =>
            url.Contains("/revision/main", StringComparison.Ordinal) && url.Contains("unsloth/", StringComparison.Ordinal) ? throw new IOException("unexpected EOF") : Answer(method, url, body));

        Assert.Equal(1, code);
        Assert.Contains("unexpected EOF", output);
        Assert.Contains("| `silero-vad` | k2-fsa/sherpa-onnx |", output);
    }

    [Fact]
    public async Task ResolvePins_AFailedSmallFileDownload_FailsOnlyThatRow()
    {
        var (code, output, _, _) = await RunAsync(answer: (method, url, body) =>
            url.EndsWith("/tokens.txt", StringComparison.Ordinal) ? throw new IOException("the download broke") : Answer(method, url, body));

        Assert.Equal(1, code);
        var rows = output.Split('\n');
        Assert.Contains("| ERROR | the download broke |", rows.First(l => l.Contains("`tokens.txt`", StringComparison.Ordinal)));
        // The other files of the repository keep what paths-info said.
        Assert.Contains($"| `{ParakeetRevision}` | `encoder.int8.onnx` | 652184281 |", rows.First(l => l.Contains("`encoder.int8.onnx`", StringComparison.Ordinal)));
        Assert.Contains("| match |", rows.First(l => l.Contains("`encoder.int8.onnx`", StringComparison.Ordinal)));
        Assert.Contains("| DRIFT |", rows.First(l => l.Contains("`decoder.int8.onnx`", StringComparison.Ordinal)));
        Assert.Contains("| match |", rows.First(l => l.Contains("`joiner.int8.onnx`", StringComparison.Ordinal)));
    }

    private static string Asset(string name, long size = 1) => "{\"name\":\"" + name + "\",\"size\":" + size + "}";

    [Fact]
    public async Task ResolvePins_PagesThroughTheReleaseAssets_WhenTheEmbeddedListLacksTheAsset()
    {
        var firstPage = "[" + string.Join(",", Enumerable.Range(0, 100).Select(i => Asset($"filler-{i}.bin"))) + "]";
        var secondPage = """[{"name":"silero_vad.onnx","size":643854,"digest":"sha256:9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6"}]""";

        var (code, output, _, api) = await RunAsync(answer: (method, url, body) => url switch
        {
            "http://api.gh.test/repos/k2-fsa/sherpa-onnx/releases/tags/asr-models" => Json("""{"id":4242,"assets":[{"name":"other.bin","size":1}]}"""),
            "http://api.gh.test/repos/k2-fsa/sherpa-onnx/releases/4242/assets?per_page=100&page=1" => Json(firstPage),
            "http://api.gh.test/repos/k2-fsa/sherpa-onnx/releases/4242/assets?per_page=100&page=2" => Json(secondPage),
            _ => Answer(method, url, body),
        });

        Assert.Equal(0, code);
        Assert.Contains("| `silero-vad` | k2-fsa/sherpa-onnx | `asr-models` | `silero_vad.onnx` | 643854 | `9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6` | match |", output);
        Assert.Contains(api.Requests, r => r.Url.EndsWith("/releases/4242/assets?per_page=100&page=2", StringComparison.Ordinal));
        Assert.DoesNotContain(api.Requests, r => r.Url.Contains("page=3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolvePins_FailsWhenTheAssetIsInNoPage()
    {
        var (code, output, _, api) = await RunAsync(answer: (method, url, body) => url switch
        {
            "http://api.gh.test/repos/k2-fsa/sherpa-onnx/releases/tags/asr-models" => Json("""{"id":7,"assets":[]}"""),
            "http://api.gh.test/repos/k2-fsa/sherpa-onnx/releases/7/assets?per_page=100&page=1" => Json("""[{"name":"x.bin","size":1}]"""),
            _ => Answer(method, url, body),
        });

        Assert.Equal(1, code);
        Assert.Contains("release asr-models has no asset silero_vad.onnx", output);
        Assert.Single(api.Requests, r => r.Url.Contains("/releases/7/assets", StringComparison.Ordinal)); // a short page ends the search
    }

    [Fact]
    public async Task ResolvePins_APagingFailure_FailsTheMissingAssetWithTheReason()
    {
        var (code, output, _, _) = await RunAsync(answer: (method, url, body) => url switch
        {
            "http://api.gh.test/repos/k2-fsa/sherpa-onnx/releases/tags/asr-models" => Json("""{"id":7,"assets":[]}"""),
            "http://api.gh.test/repos/k2-fsa/sherpa-onnx/releases/7/assets?per_page=100&page=1" => throw new IOException("pipe broke"),
            _ => Answer(method, url, body),
        });

        Assert.Equal(1, code);
        Assert.Contains("release asr-models has no asset silero_vad.onnx", output);
        Assert.Contains("pipe broke", output);
    }

    [Fact]
    public async Task ResolvePins_WritesTheJobSummary_WhenTheVariableIsSet()
    {
        var file = Path.Combine(Path.GetTempPath(), "murmur-pins-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            var (code, _, _, _) = await RunAsync(env: new Dictionary<string, string> { ["GITHUB_STEP_SUMMARY"] = file });

            Assert.Equal(0, code);
            var markdown = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            Assert.Contains("### Upstream pins", markdown);
            Assert.Contains("### Paste into ModelCatalog", markdown);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ResolvePins_PrefersTheSummaryArgumentOverTheVariable()
    {
        var chosen = Path.Combine(Path.GetTempPath(), "murmur-pins-" + Guid.NewGuid().ToString("N") + ".md");
        var ignored = chosen + ".ignored";
        try
        {
            await RunAsync(env: new Dictionary<string, string> { ["GITHUB_STEP_SUMMARY"] = ignored }, args: ["--summary", chosen]);

            Assert.True(File.Exists(chosen));
            Assert.False(File.Exists(ignored));
        }
        finally
        {
            File.Delete(chosen);
        }
    }

    [Fact]
    public async Task ResolvePins_WorksWithoutASummaryTargetAndWithoutAModelsDirectory()
    {
        var (code, output, error, _) = await RunAsync();

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Contains("All items OK.", output);
    }
}
