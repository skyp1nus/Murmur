using BenchmarkDotNet.Attributes;
using Murmur.Core.Audio;
using Murmur.Core.Import;

namespace Murmur.Benchmarks;

/// <summary>
/// The managed WAV import: 60 s of 48 kHz stereo PCM16 decoded, down-mixed and resampled into a 16 kHz mono track. The
/// per-block loop is allocation-free (pooled buffers); what is allocated is per file (reader, resampler table, streams).
/// </summary>
[MemoryDiagnoser]
public class ImportDecodeBenchmarks
{
    private string _dir = null!;
    private string _source = null!;
    private string _destination = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "murmur-bench-import", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_dir);
        _source = Path.Combine(_dir, "in.wav");
        _destination = Path.Combine(_dir, "system.wav");

        const int Rate = 48_000;
        var samples = new float[Rate * 2 * 60];
        for (var i = 0; i < samples.Length / 2; i++)
        {
            var v = MathF.Sin(2 * MathF.PI * 440 * i / Rate) * 0.5f;
            samples[2 * i] = v;
            samples[(2 * i) + 1] = -0.5f * v;
        }

        using var writer = new WavPcm16Writer(File.Create(_source), Rate, 2);
        writer.Append(samples);
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_dir, recursive: true);

    [Benchmark]
    public void DecodeWav60sStereo48k() => WavMediaDecoder.Decode(_source, _destination, null, CancellationToken.None);
}
