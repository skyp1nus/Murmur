using System.Text.Json.Serialization;

namespace Murmur.Core.Transcription.Protocol;

// The ASR worker (Murmur.Transcription) speaks JSON Lines over stdin/stdout: one UTF-8 JSON object
// per line, camelCase. The app starts it as
//   Murmur.Transcription --serve --model-dir <parakeet dir> --vad <silero_vad.onnx> [--threads N] [--idle-exit-seconds S]
// and sends WorkerRequest lines; the worker answers with WorkerEvent lines. Diagnostics go to stderr.

public static class WorkerKinds
{
    public const string Transcribe = "transcribe";
    public const string Cancel = "cancel";
    public const string Ping = "ping";
    public const string Shutdown = "shutdown";

    /// <summary>Sent once after start-up, before the model is loaded (the model loads lazily on the first transcribe).</summary>
    public const string Ready = "ready";
    public const string Progress = "progress";
    public const string Segment = "segment";
    public const string Done = "done";
    public const string Error = "error";
    public const string Pong = "pong";
}

public sealed record WorkerRequest
{
    public required string Kind { get; init; }

    /// <summary>Correlates events with the request; required for transcribe and cancel.</summary>
    public string? RequestId { get; init; }

    /// <summary>16 kHz mono WAV (PCM16 or float32). Other formats are rejected with an error event.</summary>
    public string? AudioPath { get; init; }

    /// <summary>ISO 639-1 hint or null for auto-detection. Parakeet v3 detects the language itself.</summary>
    public string? Language { get; init; }
}

public sealed record WorkerWord(string Text, double Start, double End);

/// <param name="Start">Seconds from the beginning of the audio file.</param>
public sealed record WorkerSegment(double Start, double End, string Text, IReadOnlyList<WorkerWord> Words);

public sealed record WorkerEvent
{
    public required string Kind { get; init; }

    public string? RequestId { get; init; }

    /// <summary>Progress: seconds of audio processed so far / total.</summary>
    public double ProcessedSeconds { get; init; }

    public double TotalSeconds { get; init; }

    /// <summary>Set for <see cref="WorkerKinds.Segment"/> events; segments are emitted in time order.</summary>
    public WorkerSegment? Segment { get; init; }

    public string? Error { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(WorkerRequest))]
[JsonSerializable(typeof(WorkerEvent))]
public sealed partial class WorkerJsonContext : JsonSerializerContext;
