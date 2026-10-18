using System.Text.Json.Serialization;

namespace Murmur.App.Diagnostics;

/// <summary>JSON written by <c>--autorecord</c>. Property names are camelCase (see <see cref="PerfJsonContext"/>).</summary>
public sealed record PerfReport
{
    /// <summary>False when the run used <c>--no-orb</c>: compare with an orb run to see the orb's share of the CPU.</summary>
    public bool Orb { get; init; } = true;

    /// <summary>Wall-clock seconds of the sampled recording window.</summary>
    public double DurationS { get; init; }

    /// <summary>Process CPU over the idle window, % of the whole machine.</summary>
    public double IdleCpuPct { get; init; }

    /// <summary>Mean process CPU while recording, % of the whole machine.</summary>
    public double CpuAvgPct { get; init; }

    public double CpuP95Pct { get; init; }

    /// <summary>Mean process CPU while recording, % of one core.</summary>
    public double CpuAvgPctOneCore { get; init; }

    public double WsAvgMb { get; init; }

    public double WsMaxMb { get; init; }

    public double PrivateMaxMb { get; init; }

    public PerfRecorderStats RecorderStats { get; init; } = new();

    public PerfSession Session { get; init; } = new();

    /// <summary>Set only on failure; the process then exits non-zero.</summary>
    public string? Error { get; init; }
}

public sealed record PerfRecorderStats
{
    public long MicrophoneFrames { get; init; }

    public long SystemFrames { get; init; }

    public long DroppedFrames { get; init; }

    public long ScreenFramesDropped { get; init; }
}

public sealed record PerfSession
{
    public string Id { get; init; } = string.Empty;

    public string Folder { get; init; } = string.Empty;

    public double MicSeconds { get; init; }

    public double SystemSeconds { get; init; }

    public int ScreenSegments { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PerfReport))]
internal sealed partial class PerfJsonContext : JsonSerializerContext
{
}
