using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Murmur.Transcription.Audio;
using Murmur.Transcription.Native;
using Murmur.Transcription.Pipeline;

namespace Murmur.Transcription.Bench;

internal sealed record MachineInfo(string Os, string Cpu, int Cores, string Architecture);

internal sealed record BenchResult
{
    public string Mode { get; init; } = "transcribe";

    public double AudioSeconds { get; init; }

    public double LoadMs { get; init; }

    public double TranscribeMs { get; init; }

    /// <summary>Real-time factor: transcription time / audio duration (lower is better).</summary>
    public double Rtf { get; init; }

    /// <summary>Audio duration / transcription time ("N× faster than real time").</summary>
    public double RealtimeFactorX { get; init; }

    /// <summary>CPU time (user + kernel, all threads) spent while transcribing; independent of other load.</summary>
    public double CpuMs { get; init; }

    /// <summary>CpuMs / TranscribeMs: average number of cores the worker kept busy.</summary>
    public double CpuUtilization { get; init; }

    /// <summary>1-minute load average before/after (Linux): other processes competing for the CPUs.</summary>
    public double LoadAverageBefore { get; init; }

    public double LoadAverageAfter { get; init; }

    public int Segments { get; init; }

    /// <summary>Total length of the VAD segments (only reported in vad-only mode).</summary>
    public double SpeechSeconds { get; init; }

    public int Words { get; init; }

    /// <summary>CPU budget (--threads) = Parallel decode workers x OrtThreads.</summary>
    public int Threads { get; init; }

    public int OrtThreads { get; init; }

    public int Batch { get; init; }

    public int Parallel { get; init; }

    public double PeakRssMb { get; init; }

    public string Native { get; init; } = string.Empty;

    public string Runtime { get; init; } = string.Empty;

    public required MachineInfo Machine { get; init; }

    public IReadOnlyList<string> Wavs { get; init; } = [];

    public string FirstText { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(BenchResult))]
internal sealed partial class BenchJsonContext : JsonSerializerContext;

/// <summary>
/// <c>--bench</c>: synthesises N seconds of 16 kHz audio from short clips (deterministic 0.5–1.5 s gaps) and runs the
/// production pipeline over it, reporting load time, transcription time and real-time factor.
/// </summary>
internal static class BenchRunner
{
    public static BenchResult Run(CliOptions options, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(options);
        var clips = new List<float[]>();
        foreach (var wav in options.BenchWavs)
        {
            clips.Add(WavFile.ReadAs16kMono(wav));
        }

        var totalSamples = (long)Math.Round(options.BenchSeconds * WavReader.RequiredSampleRate);
        ISampleSource source = new ConcatenatedSource(clips, totalSamples);
        if (!string.IsNullOrEmpty(options.SaveWavPath))
        {
            WavFile.Write(options.SaveWavPath, source);
            source.Dispose();
            source = WavReader.Open(options.SaveWavPath);
            log.WriteLine($"bench: signal written to {options.SaveWavPath}; transcribing from the file");
        }

        using var _ = source;
        if (options.VadOnly)
        {
            return RunVadOnly(options, source, log);
        }

        using var engine = new SherpaTranscriptionEngine(Program.EngineOptionsFrom(options));

        log.WriteLine($"bench: {options.BenchSeconds:0} s from {clips.Count} clip(s), threads={options.Threads} ({options.EffectiveParallelism} worker(s) x {options.OrtThreadsPerWorker}), batch={options.BatchSize}");
        engine.EnsureLoaded();
        log.WriteLine($"bench: model loaded in {engine.LoadMilliseconds:0} ms");

        var firstText = string.Empty;
        var lastReport = Stopwatch.GetTimestamp();
        var cpuBefore = CpuTime();
        var loadBefore = LoadAverage();
        var sw = Stopwatch.StartNew();
        var stats = engine.Transcribe(
            source,
            s =>
            {
                if (firstText.Length == 0)
                {
                    firstText = s.Text;
                }
            },
            (processed, total) =>
            {
                if (Stopwatch.GetElapsedTime(lastReport).TotalSeconds >= 10)
                {
                    lastReport = Stopwatch.GetTimestamp();
                    log.WriteLine($"bench: {processed:0} / {total:0} s audio after {sw.Elapsed.TotalSeconds:0} s");
                }
            },
            CancellationToken.None);
        var transcribeMs = sw.Elapsed.TotalMilliseconds;
        var cpuMs = (CpuTime() - cpuBefore).TotalMilliseconds;

        return new BenchResult
        {
            AudioSeconds = stats.AudioSeconds,
            LoadMs = Math.Round(engine.LoadMilliseconds, 1),
            TranscribeMs = Math.Round(transcribeMs, 1),
            Rtf = Math.Round(transcribeMs / 1000.0 / stats.AudioSeconds, 5),
            RealtimeFactorX = Math.Round(stats.AudioSeconds / (transcribeMs / 1000.0), 2),
            CpuMs = Math.Round(cpuMs, 1),
            CpuUtilization = Math.Round(cpuMs / transcribeMs, 2),
            LoadAverageBefore = loadBefore,
            LoadAverageAfter = LoadAverage(),
            Segments = stats.Segments,
            Words = stats.Words,
            Threads = options.Threads,
            Batch = options.BatchSize,
            Parallel = options.EffectiveParallelism,
            OrtThreads = options.OrtThreadsPerWorker,
            PeakRssMb = Math.Round(PeakRssBytes() / (1024.0 * 1024.0), 1),
            Native = OfflineRecognizer.NativeVersion,
            Runtime = RuntimeInformation.FrameworkDescription + (RuntimeFeatureInfo.IsNativeAot ? " (Native AOT)" : " (JIT)"),
            Machine = Machine(),
            Wavs = [.. options.BenchWavs.Select(Path.GetFileName).OfType<string>()],
            FirstText = firstText,
        };
    }

    /// <summary>Diagnostics: cost of reading + Silero VAD alone (no ASR model loaded).</summary>
    private static BenchResult RunVadOnly(CliOptions options, ISampleSource source, TextWriter log)
    {
        var engineOptions = Program.EngineOptionsFrom(options);
        var segments = 0;
        double speech = 0;
        var sw = Stopwatch.StartNew();
        using (var vad = new SileroVad(VoiceActivityDetector.CreateSilero(engineOptions.VadModelPath, engineOptions.Pipeline.Vad)))
        {
            SpeechSegmenter.Run(source, vad, engineOptions.Pipeline, s =>
            {
                segments++;
                speech += s.Length / (double)WavReader.RequiredSampleRate;
                s.Release();
            }, null, CancellationToken.None);
        }

        var ms = sw.Elapsed.TotalMilliseconds;
        var audio = source.TotalSamples / (double)WavReader.RequiredSampleRate;
        log.WriteLine($"bench: VAD only, {segments} segments, {speech:0.0} s speech of {audio:0.0} s");
        return new BenchResult
        {
            Mode = "vad-only",
            AudioSeconds = audio,
            TranscribeMs = Math.Round(ms, 1),
            Rtf = Math.Round(ms / 1000.0 / audio, 5),
            RealtimeFactorX = Math.Round(audio / (ms / 1000.0), 2),
            Segments = segments,
            SpeechSeconds = Math.Round(speech, 1),
            PeakRssMb = Math.Round(PeakRssBytes() / (1024.0 * 1024.0), 1),
            Native = OfflineRecognizer.NativeVersion,
            Runtime = RuntimeInformation.FrameworkDescription + (RuntimeFeatureInfo.IsNativeAot ? " (Native AOT)" : " (JIT)"),
            Machine = Machine(),
        };
    }

    public static string ToJson(BenchResult result) => JsonSerializer.Serialize(result, BenchJsonContext.Default.BenchResult);

    public static MachineInfo Machine() =>
        new(RuntimeInformation.OSDescription, CpuName(), Environment.ProcessorCount, RuntimeInformation.ProcessArchitecture.ToString());

    private static TimeSpan CpuTime()
    {
        using var p = Process.GetCurrentProcess();
        return p.TotalProcessorTime;
    }

    private static double LoadAverage()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/loadavg"))
            {
                var text = File.ReadAllText("/proc/loadavg");
                var space = text.IndexOf(' ', StringComparison.Ordinal);
                if (space > 0 && double.TryParse(text.AsSpan(0, space), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                {
                    return v;
                }
            }
        }
        catch (IOException)
        {
        }

        return -1;
    }

    /// <summary>Peak resident set (VmHWM on Linux, peak working set on Windows).</summary>
    public static long PeakRssBytes()
    {
        using var p = Process.GetCurrentProcess();
        return p.PeakWorkingSet64;
    }

    private static string CpuName()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
            {
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.Ordinal))
                    {
                        var colon = line.IndexOf(':', StringComparison.Ordinal);
                        return colon >= 0 ? line[(colon + 1)..].Trim() : line;
                    }
                }
            }

            if (Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") is { Length: > 0 } id)
            {
                return id;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return "unknown";
    }
}

internal static class RuntimeFeatureInfo
{
    /// <summary>True when running as a Native AOT binary (no JIT available).</summary>
    public static bool IsNativeAot => !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled;
}
