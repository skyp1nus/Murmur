using System.Diagnostics;
using Murmur.Core.Transcription.Protocol;
using Murmur.Transcription.Audio;
using Murmur.Transcription.Native;

namespace Murmur.Transcription.Pipeline;

internal sealed record EngineOptions
{
    public required string ModelDirectory { get; init; }

    public required string VadModelPath { get; init; }

    /// <summary>ONNX Runtime intra-op threads of the recognizer (per concurrent decode call).</summary>
    public int Threads { get; init; } = 1;

    public PipelineOptions Pipeline { get; init; } = new();
}

/// <summary>One transcription job at a time; implemented by <see cref="SherpaTranscriptionEngine"/>, faked in tests.</summary>
internal interface ITranscriptionEngine : IDisposable
{
    /// <exception cref="InvalidDataException">The file is not a 16 kHz mono PCM16/float32 WAV.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled (checked between batches).</exception>
    PipelineStats Transcribe(string audioPath, Action<WorkerSegment> onSegment, Action<double, double>? onProgress, CancellationToken cancellationToken);
}

/// <summary>
/// Parakeet + Silero over sherpa-onnx. The ASR model is loaded lazily on the first job (after the WAV header has been
/// validated, so a bad file never costs a model load) and lives until the engine is disposed / the process exits.
/// </summary>
internal sealed class SherpaTranscriptionEngine(EngineOptions options) : ITranscriptionEngine
{
    private OfflineRecognizer? _recognizer;

    public EngineOptions Options { get; } = options;

    /// <summary>Wall-clock time of the model load, or 0 before the first job.</summary>
    public double LoadMilliseconds { get; private set; }

    public bool IsLoaded => _recognizer is not null;

    public void EnsureLoaded()
    {
        if (_recognizer is not null)
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        _recognizer = OfflineRecognizer.CreateParakeet(new ParakeetModelFiles(Options.ModelDirectory), Math.Max(1, Options.Threads));
        LoadMilliseconds = sw.Elapsed.TotalMilliseconds;
    }

    public PipelineStats Transcribe(string audioPath, Action<WorkerSegment> onSegment, Action<double, double>? onProgress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(audioPath);
        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException("Audio file not found: " + audioPath, audioPath);
        }

        using var reader = WavReader.Open(audioPath);
        return Transcribe(reader, onSegment, onProgress, cancellationToken);
    }

    public PipelineStats Transcribe(ISampleSource source, Action<WorkerSegment> onSegment, Action<double, double>? onProgress, CancellationToken cancellationToken)
    {
        if (!File.Exists(Options.VadModelPath))
        {
            throw new FileNotFoundException("Silero VAD model not found: " + Options.VadModelPath, Options.VadModelPath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureLoaded();
        cancellationToken.ThrowIfCancellationRequested();

        var pipeline = new TranscriptionPipeline(
            new NativeSpeechRecognizer(_recognizer!),
            () => new SileroVad(VoiceActivityDetector.CreateSilero(Options.VadModelPath, Options.Pipeline.Vad)),
            Options.Pipeline);
        return pipeline.Run(source, onSegment, onProgress, cancellationToken);
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _recognizer = null;
    }
}
