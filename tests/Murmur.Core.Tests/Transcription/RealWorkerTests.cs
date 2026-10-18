using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Tests.Fakes;
using Murmur.Core.Transcription;

namespace Murmur.Core.Tests.Transcription;

/// <summary>
/// <see cref="WorkerAsrClient"/> against the real <c>Murmur.Transcription</c> worker and Parakeet models. Skipped
/// unless the models (<c>$MURMUR_MODELS_DIR</c> or <c>/root/models</c>) and a built worker
/// (<c>$MURMUR_TRANSCRIPTION_WORKER</c> or <c>src/Murmur.Transcription/bin/&lt;config&gt;/net11.0</c>) are present.
/// </summary>
public sealed class RealWorkerTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static string ModelsRoot =>
        Environment.GetEnvironmentVariable(ModelLocator.ModelsDirectoryVariable) is { Length: > 0 } dir ? dir : "/root/models";

    [Fact]
    [Trait("Category", "RealModels")]
    public async Task RealWorker_TranscribesTheEnglishTestClip()
    {
        var models = new ModelLocator(ModelsRoot);
        Assert.SkipUnless(models.IsAsrInstalled, $"Parakeet models not installed under {ModelsRoot} (set {ModelLocator.ModelsDirectoryVariable})");
        var worker = FindWorker();
        Assert.SkipUnless(worker is not null, $"Murmur.Transcription is not built (set {WorkerAsrClient.WorkerPathVariable})");
        var clip = Path.Combine(models.ParakeetDirectory, "test_wavs", "en.wav");
        Assert.SkipUnless(File.Exists(clip), "test clip missing: " + clip);

        var wav = To16kMono(clip, _dir.File("en-16k.wav"));
        await using var client = new WorkerAsrClient(models, new WorkerAsrClientOptions(worker, Threads: Math.Min(4, Environment.ProcessorCount)));
        var progress = new List<AsrProgress>();
        var segments = new List<TranscriptSegment>();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await foreach (var s in client.TranscribeAsync(new AsrRequest(wav, Speaker.Others), new SyncProgress(progress), timeout.Token))
        {
            segments.Add(s);
        }

        Assert.NotEmpty(segments);
        var text = string.Join(' ', segments.Select(s => s.Text));
        Assert.Contains("country", text, StringComparison.OrdinalIgnoreCase);
        Assert.All(segments, s =>
        {
            Assert.Equal(Speaker.Others, s.Speaker);
            Assert.True(s.End >= s.Start);
            Assert.NotEmpty(s.Words);
        });
        for (var i = 1; i < segments.Count; i++)
        {
            Assert.True(segments[i].Start >= segments[i - 1].Start);
        }

        Assert.NotEmpty(progress);
        Assert.Equal(AsrStatus.Idle, client.Status);
    }

    /// <summary>The worker dll next to the solution's build output (same configuration as these tests first).</summary>
    private static string? FindWorker()
    {
        if (Environment.GetEnvironmentVariable(WorkerAsrClient.WorkerPathVariable) is { Length: > 0 } configured)
        {
            return File.Exists(configured) ? configured : null;
        }

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Murmur.sln")))
        {
            root = root.Parent;
        }

        if (root is null)
        {
            return null;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name; // bin/<config>/<tfm>/
        foreach (var config in new[] { current, "Release", "Debug" })
        {
            if (config is null)
            {
                continue;
            }

            var dll = Path.Combine(root.FullName, "src", "Murmur.Transcription", "bin", config, "net11.0", "Murmur.Transcription.dll");
            if (File.Exists(dll))
            {
                return dll;
            }
        }

        return null;
    }

    private static string To16kMono(string source, string destination)
    {
        var (format, samples) = WavReader.ReadAll(source);
        var resampler = new StreamingResampler(format, AudioFormat.Asr.SampleRate);
        var output = new float[resampler.MaxOutputSamples(samples.Length / format.Channels) + resampler.MaxFlushSamples];
        var written = resampler.Process(samples, output);
        written += resampler.Flush(output.AsSpan(written));
        return TestWav.Write(destination, output.AsSpan(0, written));
    }

    private sealed class SyncProgress(List<AsrProgress> values) : IProgress<AsrProgress>
    {
        public void Report(AsrProgress value)
        {
            lock (values)
            {
                values.Add(value);
            }
        }
    }
}
