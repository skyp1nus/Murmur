using BenchmarkDotNet.Attributes;
using Murmur.App.Controls;

namespace Murmur.Benchmarks;

/// <summary>
/// The per-frame work of a strip of level bars on the render thread (#99): where the strip is, and the layout of every bar of the
/// frame, plus a new sample now and then. It runs at the display rate (60 to 144 Hz) for each strip on screen while it slides.
/// Budget: no allocation, well under a microsecond per frame.
/// </summary>
[MemoryDiagnoser]
public class LevelBarsBenchmarks
{
    // The Tracks card's strip and the onboarding meter, as the controls define them.
    private static readonly LevelBarsStyle History = new(LevelBarsShape.History, 26, 10, 3, 4, 32, 4, 0.25);
    private static readonly LevelBarsStyle Meter = new(LevelBarsShape.Meter, 40, 0, 2, 4, 0, 3, 0.05);

    private LevelBarsMotion _history = null!;
    private LevelBarsMotion _meter = null!;
    private LevelBar[] _bars = null!;
    private double _now;
    private int _frame;

    [GlobalSetup]
    public void Setup()
    {
        _history = new LevelBarsMotion(History);
        _meter = new LevelBarsMotion(Meter);
        var values = new float[40];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (i % 7) / 6f;
        }

        _history.Reset(values, 0);
        _meter.Reset(values, 0);
        _bars = new LevelBar[Math.Max(History.MaxRects, Meter.MaxRects)];
    }

    /// <summary>One 144 Hz frame of the Tracks card strip (a sample every 36 frames).</summary>
    [Benchmark]
    public int HistoryFrame()
    {
        _now += 1.0 / 144;
        if (++_frame % 36 == 0)
        {
            _history.Push((_frame % 5) / 4f, _now, animate: true);
        }

        return _history.Layout(_bars, 360, 36, _history.Position(_now), idle: false);
    }

    /// <summary>One 144 Hz frame of the meter (a sample every 7 frames).</summary>
    [Benchmark]
    public int MeterFrame()
    {
        _now += 1.0 / 144;
        if (++_frame % 7 == 0)
        {
            _meter.Push((_frame % 5) / 4f, _now, animate: true);
        }

        return _meter.Layout(_bars, 200, 24, _meter.Position(_now), idle: false);
    }
}
