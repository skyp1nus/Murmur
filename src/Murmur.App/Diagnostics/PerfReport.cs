using System.Text.Json.Serialization;

namespace Murmur.App.Diagnostics;

/// <summary>JSON written by <c>--autorecord</c>. Property names are camelCase (see <see cref="PerfJsonContext"/>).</summary>
public sealed record PerfReport
{
    /// <summary>False when the run used <c>--no-orb</c>: compare with an orb run to see the orb's share of the CPU.</summary>
    public bool Orb { get; init; } = true;

    /// <summary>
    /// Length of the session timeline in seconds: <see cref="Murmur.Core.Recording.IRecorder.Elapsed"/> (session time
    /// since the audio started) read as the stop is requested. The audio files, and every screen segment's
    /// offset/duration, are on this timeline.
    /// </summary>
    public double DurationS { get; init; }

    /// <summary>
    /// Milliseconds starting the screen took: session time when the recorder's start returned minus the first screen
    /// segment's <see cref="PerfScreenSegment.OffsetS"/> (the moment the recorder asked the screen to start). Omitted
    /// when the session has no screen segment.
    /// </summary>
    public double? ScreenStartMs { get; init; }

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

    /// <summary>
    /// Working set growth over the recording: a least-squares slope of the per-second samples, in MB per minute. A
    /// healthy recorder plateaus (about 0); a leak shows as a steady positive slope.
    /// </summary>
    public double WsSlopeMbPerMin { get; init; }

    /// <summary>Private bytes growth over the recording, MB per minute (least-squares slope).</summary>
    public double PrivateSlopeMbPerMin { get; init; }

    /// <summary>Largest managed heap seen while recording (<see cref="GC.GetTotalMemory(bool)"/>, no forced GC).</summary>
    public double GcHeapMaxMb { get; init; }

    /// <summary>Managed bytes allocated while recording (<see cref="GC.GetTotalAllocatedBytes(bool)"/> delta).</summary>
    public double GcAllocatedMb { get; init; }

    /// <summary>Managed bytes the UI thread allocated while recording (Avalonia layout, rendering, timers, view models).</summary>
    public double UiThreadAllocatedMb { get; init; }

    /// <summary>Managed bytes the perf sampler itself allocated while recording (part of <see cref="GcAllocatedMb"/>).</summary>
    public double SamplerAllocatedMb { get; init; }

    /// <summary>
    /// Memory every <see cref="AutoRecordRunner.MemorySampleEvery"/> samples, to tell managed growth (GC heap) from
    /// native growth (private bytes minus GC committed).
    /// </summary>
    public IReadOnlyList<PerfMemorySample>? Memory { get; init; }

    public PerfRecorderStats RecorderStats { get; init; } = new();

    public PerfSession Session { get; init; } = new();

    /// <summary>
    /// Non-fatal problems of a run that still recorded (e.g. Windows denied the microphone, so only system audio was
    /// recorded); omitted when there were none. The exit code stays 0.
    /// </summary>
    public IReadOnlyList<string>? Warnings { get; init; }

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

/// <summary>One memory reading of a recording (all sizes in MB).</summary>
public sealed record PerfMemorySample
{
    /// <summary>Seconds since the recording started.</summary>
    public double Seconds { get; init; }

    public double WsMb { get; init; }

    public double PrivateMb { get; init; }

    /// <summary>Managed bytes in use (<see cref="GC.GetTotalMemory(bool)"/>, no forced GC).</summary>
    public double GcHeapMb { get; init; }

    /// <summary>Memory the GC has committed (<see cref="GCMemoryInfo.TotalCommittedBytes"/> of the last GC).</summary>
    public double GcCommittedMb { get; init; }

    /// <summary>Managed bytes allocated since the recording started.</summary>
    public double GcAllocatedMb { get; init; }

    public int Gen0Collections { get; init; }

    public int Gen2Collections { get; init; }
}

public sealed record PerfSession
{
    public string Id { get; init; } = string.Empty;

    public string Folder { get; init; } = string.Empty;

    public double MicSeconds { get; init; }

    public double SystemSeconds { get; init; }

    /// <summary>Number of screen segments (and files) of the session.</summary>
    public int ScreenSegments { get; init; }

    /// <summary>
    /// The session's screen segments on the session timeline, to compare with the MP4 files: a file's media duration
    /// must equal <see cref="PerfScreenSegment.DurationS"/> (scripts/check-recording.ps1 does).
    /// </summary>
    public IReadOnlyList<PerfScreenSegment> ScreenSegmentTimes { get; init; } = [];
}

/// <summary>One screen segment of the session: <c>screen-NNN.mp4</c> with <see cref="Index"/> NNN.</summary>
public sealed record PerfScreenSegment
{
    public int Index { get; init; }

    /// <summary>Session time of media time 0 of the file.</summary>
    public double OffsetS { get; init; }

    /// <summary>Session time the segment covers; equals the file's media duration.</summary>
    public double DurationS { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PerfReport))]
internal sealed partial class PerfJsonContext : JsonSerializerContext
{
}
