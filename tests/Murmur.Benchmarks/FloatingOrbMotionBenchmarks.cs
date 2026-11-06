using BenchmarkDotNet.Attributes;
using Murmur.App.Controls;

namespace Murmur.Benchmarks;

/// <summary>
/// The tick of the floating recorder orb (#46), 15 times a second for as long as a recording is on screen: the angle and the ping
/// ring as maths of the elapsed time. Must allocate nothing and cost next to nothing.
/// </summary>
[MemoryDiagnoser]
public class FloatingOrbMotionBenchmarks
{
    private double _seconds;

    [Benchmark]
    public double Angle()
    {
        _seconds += 1.0 / 15;
        return FloatingOrbMotion.Angle(_seconds);
    }

    [Benchmark]
    public double PingWhileOut()
    {
        FloatingOrbMotion.Ping(0.9, out var scale, out var opacity);
        return scale + opacity;
    }

    [Benchmark]
    public bool PingAtRest() => FloatingOrbMotion.Ping(2.4, out _, out _);
}
