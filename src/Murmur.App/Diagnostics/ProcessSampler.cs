using System.Diagnostics;

namespace Murmur.App.Diagnostics;

/// <summary>One reading of the process: CPU since the previous reading, and memory now.</summary>
/// <param name="CpuMachinePct">CPU time delta / wall delta / logical processors, in percent (100 = whole machine).</param>
/// <param name="CpuCorePct">CPU time delta / wall delta, in percent of one core (can exceed 100).</param>
public readonly record struct ProcessSample(double CpuMachinePct, double CpuCorePct, double WorkingSetMb, double PrivateMb);

/// <summary>Samples the current process. Not thread-safe; use from one sampling thread.</summary>
public sealed class ProcessSampler : IDisposable
{
    private const double BytesPerMb = 1024.0 * 1024.0;

    private readonly Process _process = Process.GetCurrentProcess();
    private readonly int _processors = Environment.ProcessorCount;
    private TimeSpan _lastCpu;
    private long _lastTimestamp;

    public ProcessSampler() => Reset();

    /// <summary>Starts a new measuring interval now.</summary>
    public void Reset()
    {
        _process.Refresh();
        _lastCpu = _process.TotalProcessorTime;
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Reads CPU since the last <see cref="Reset"/>/<see cref="Sample"/> and the current memory.</summary>
    public ProcessSample Sample()
    {
        _process.Refresh();
        var cpu = _process.TotalProcessorTime;
        var now = Stopwatch.GetTimestamp();

        var wall = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        var core = wall > 0 ? (cpu - _lastCpu).TotalSeconds / wall * 100.0 : 0;
        _lastCpu = cpu;
        _lastTimestamp = now;

        return new ProcessSample(core / _processors, core, _process.WorkingSet64 / BytesPerMb, _process.PrivateMemorySize64 / BytesPerMb);
    }

    public void Dispose() => _process.Dispose();

    /// <summary>Nearest-rank percentile of <paramref name="values"/> (<paramref name="percentile"/> in (0, 100]).</summary>
    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = new double[values.Count];
        for (var i = 0; i < sorted.Length; i++)
        {
            sorted[i] = values[i];
        }

        Array.Sort(sorted);
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}
