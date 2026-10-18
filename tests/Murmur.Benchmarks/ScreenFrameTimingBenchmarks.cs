using BenchmarkDotNet.Attributes;
using Murmur.Capture.Windows.Screen;

namespace Murmur.Benchmarks;

/// <summary>
/// The per-frame timing math of the screen recorder (src/Murmur.Capture.Windows/Screen/FrameTiming.cs, linked as a
/// source file: it is pure and has no Windows APIs): the FrameArrived rate limiter, then the encoder thread's segment
/// timeline and held-frame timing. One operation = one second of a 144 Hz desktop recorded at 30 fps. Must allocate 0 B.
/// </summary>
[MemoryDiagnoser]
public class ScreenFrameTimingBenchmarks
{
    private const int RefreshHz = 144;
    private const int FrameRate = 30;

    private FrameRateLimiter _limiter = null!;
    private SegmentTimeline _timeline = null!;
    private HeldFrameTiming _timing = null!;
    private long _vsync;

    [GlobalSetup]
    public void Setup()
    {
        _limiter = new FrameRateLimiter(FrameRate);
        _timeline = new SegmentTimeline();
        _timing = new HeldFrameTiming(HeldFrameTiming.SinkTimescale(FrameRate));
        _timeline.Reset(0);
    }

    [Benchmark(OperationsPerInvoke = RefreshHz)]
    public long OneSecondOfFrames()
    {
        long sum = 0;
        for (var i = 0; i < RefreshHz; i++)
        {
            var ticks = _vsync++ * MediaTime.TicksPerSecond / RefreshHz;
            if (_limiter.TryAccept(ticks)
                && _timing.Advance(_timeline.ToSampleTime(ticks), out var time, out var duration))
            {
                sum += time + duration;
            }
        }

        return sum;
    }
}
