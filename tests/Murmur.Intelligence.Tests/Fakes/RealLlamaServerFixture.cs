using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Murmur.Intelligence.Tests.Fakes;

/// <summary>
/// Provides a real <c>llama-server</c>: <c>MURMUR_LLAMA_SERVER</c> if set, otherwise the llama.cpp b11716 Ubuntu x64
/// release downloaded into a temp folder (deleted afterwards). <see cref="SkipReason"/> explains why it is missing.
/// </summary>
public sealed class RealLlamaServerFixture : IAsyncLifetime
{
    private const string ReleaseUrl =
        "https://github.com/ggml-org/llama.cpp/releases/download/b11716/llama-b11716-bin-ubuntu-x64.tar.gz";

    private string? _downloadDirectory;

    public string? ServerPath { get; private set; }

    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("MURMUR_LLAMA_SERVER");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                ServerPath = configured;
            }
            else
            {
                SkipReason = $"MURMUR_LLAMA_SERVER points to a missing file: {configured}";
            }

            return;
        }

        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            SkipReason = "The downloadable llama-server test binary is Linux x64 only; set MURMUR_LLAMA_SERVER to test elsewhere.";
            return;
        }

        _downloadDirectory = Path.Combine(Path.GetTempPath(), "murmur-llama-server-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_downloadDirectory);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            await using (var download = await http.GetStreamAsync(ReleaseUrl))
            await using (var gzip = new GZipStream(download, CompressionMode.Decompress))
            {
                await TarFile.ExtractToDirectoryAsync(gzip, _downloadDirectory, overwriteFiles: true);
            }

            var server = Directory.EnumerateFiles(_downloadDirectory, "llama-server", SearchOption.AllDirectories).FirstOrDefault();
            if (server is null)
            {
                SkipReason = "The llama.cpp release archive has no llama-server binary.";
            }
            else
            {
                ServerPath = server;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            SkipReason = $"llama.cpp release is not reachable ({ex.GetType().Name}: {ex.Message}); skipping the real-binary test.";
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_downloadDirectory is not null)
        {
            try
            {
                Directory.Delete(_downloadDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }
}
