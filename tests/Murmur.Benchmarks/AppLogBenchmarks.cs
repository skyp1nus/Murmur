using BenchmarkDotNet.Attributes;
using Murmur.Core.Diagnostics;
using Murmur.Storage;

namespace Murmur.Benchmarks;

/// <summary>
/// The ring path of the diagnostic log (#100): an <see cref="LogLevel.Info"/> event with constant strings is one short lock and a
/// struct store into the preallocated ring, so it must allocate nothing and never touch the disk.
/// </summary>
[MemoryDiagnoser]
public class AppLogBenchmarks
{
    private const string Detail = "constant";
    private string _root = null!;
    private FileAppLog _log = null!;

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "murmur-bench-log-" + Guid.NewGuid().ToString("N"));
        _log = new FileAppLog(new StoragePaths(_root), TimeProvider.System);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _log.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Benchmark]
    public void WriteInfo() => _log.Write(LogLevel.Info, "bench", "write", Detail);

    [Benchmark(Baseline = true)]
    public void WriteToNullLog() => NullAppLog.Instance.Write(LogLevel.Info, "bench", "write", Detail);
}
