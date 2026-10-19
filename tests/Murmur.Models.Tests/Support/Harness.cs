using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Murmur.Core.Models;

namespace Murmur.Models.Tests.Support;

/// <summary>A tar entry for archives that are not what they should be.</summary>
internal sealed record TarSpec(string Name, TarEntryType Type = TarEntryType.RegularFile, string? LinkName = null, byte[]? Content = null);

/// <summary>A temp models directory plus a fake server, and builders for managers and catalog items pointing at it.</summary>
internal sealed class Harness : IAsyncDisposable
{
    public Harness()
    {
        Root = Path.Combine(Path.GetTempPath(), "murmur-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public FakeFileServer Server { get; } = new();

    public HttpClient Http { get; } = new() { Timeout = Timeout.InfiniteTimeSpan };

    public ModelCatalogSources Sources => new(Server.BaseAddress, Server.BaseAddress);

    public string Models => Path.Combine(Root, "models");

    public static byte[] Bytes(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static string Sha256(byte[] content) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

    public static ModelCatalogItem FileItem(
        string id,
        FakeFileServer server,
        string path,
        string targetPath,
        ModelKind kind = ModelKind.Chat,
        string? sha256 = null,
        long? sizeBytes = null,
        long displaySize = 1000,
        string? repo = "org/repo") =>
        new(
            new ModelInfo(id, kind, id, displaySize, Required: false),
            targetPath,
            IsDirectory: false,
            [new ModelFileSpec(server.Url(path), Path.GetFileName(targetPath), sha256, sizeBytes)],
            Repo: repo);

    public static ModelCatalogItem DirectoryItem(string id, FakeFileServer server, string target, params string[] paths) =>
        new(
            new ModelInfo(id, ModelKind.Asr, id, 1000, Required: true),
            target,
            IsDirectory: true,
            [.. paths.Select(p => new ModelFileSpec(server.Url(p), Path.GetFileName(p)))]);

    public static ModelCatalogItem ArchiveItem(string id, FakeFileServer server, string path, ModelPackaging packaging, string executable) =>
        new(
            new ModelInfo(id, ModelKind.LlmRuntime, id, 1000, Required: false),
            "llama.cpp",
            IsDirectory: true,
            [new ModelFileSpec(server.Url(path), Path.GetFileName(path))],
            packaging,
            executable);

    public ModelManager Manager(
        IReadOnlyList<ModelCatalogItem>? items = null,
        int retries = 3,
        TimeSpan? stall = null,
        Func<string, long>? freeSpace = null,
        TimeSpan? progressInterval = null,
        Func<long>? memory = null,
        bool reportHashes = false,
        Action<string>? beforeRemove = null) =>
        new(new ModelManagerOptions
        {
            ModelsDirectory = Models,
            Catalog = items ?? ModelCatalog.Create(Sources, ModelPlatform.Linux),
            HttpClient = Http,
            RetryDelays = [.. Enumerable.Repeat(TimeSpan.FromMilliseconds(1), retries)],
            StallTimeout = stall ?? TimeSpan.FromSeconds(30),
            FreeSpaceProbe = freeSpace,
            ProgressInterval = progressInterval ?? TimeSpan.FromMilliseconds(100),
            TotalMemoryProbe = memory,
            ReportHashes = reportHashes,
            BeforeRemove = beforeRemove,
        });

    public static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        return stream.ToArray();
    }

    public static byte[] TarGz(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            foreach (var (name, content) in entries)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(content),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                };
                tar.WriteEntry(entry);
            }
        }

        return stream.ToArray();
    }

    public static byte[] TarGz(params TarSpec[] entries)
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            foreach (var spec in entries)
            {
                var entry = new PaxTarEntry(spec.Type, spec.Name) { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead };
                if (spec.Type is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    entry.LinkName = spec.LinkName!;
                }
                else if (spec.Type == TarEntryType.RegularFile)
                {
                    entry.DataStream = new MemoryStream(spec.Content ?? []);
                }

                tar.WriteEntry(entry);
            }
        }

        return stream.ToArray();
    }

    public static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Condition not met in time");
            }

            await Task.Delay(5);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        Http.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
