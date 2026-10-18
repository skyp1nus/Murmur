using BenchmarkDotNet.Attributes;
using Murmur.Core.Sessions;

namespace Murmur.Benchmarks;

[MemoryDiagnoser]
public class SessionIdBenchmarks
{
    [Benchmark]
    public SessionId New() => SessionId.New();

    [Benchmark]
    public string NewToString() => SessionId.New().ToString();
}
