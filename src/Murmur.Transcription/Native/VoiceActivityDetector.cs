using System.Buffers;

namespace Murmur.Transcription.Native;

/// <summary>Silero VAD settings (seconds). Defaults follow the task spec: segments stay well under 20 s.</summary>
internal sealed record VadOptions
{
    public float Threshold { get; init; } = 0.5f;

    public float MinSilenceSeconds { get; init; } = 0.3f;

    public float MinSpeechSeconds { get; init; } = 0.25f;

    /// <summary>Above this the detector raises its threshold to force a split (sherpa-onnx behaviour).</summary>
    public float MaxSpeechSeconds { get; init; } = 15f;

    /// <summary>Silero v4/v5 at 16 kHz uses 512-sample windows.</summary>
    public int WindowSize { get; init; } = 512;

    /// <summary>Internal ring buffer of the detector; must exceed the longest segment.</summary>
    public float BufferSeconds { get; init; } = 60f;
}

/// <summary>A speech segment copied out of the detector into a pooled buffer; return it with <see cref="Release"/>.</summary>
internal readonly struct PooledSegment
{
    public PooledSegment(long startSample, float[] buffer, int length)
    {
        StartSample = startSample;
        Buffer = buffer;
        Length = length;
    }

    public long StartSample { get; }

    public float[] Buffer { get; }

    public int Length { get; }

    public ReadOnlyMemory<float> Samples => Buffer.AsMemory(0, Length);

    public void Release() => ArrayPool<float>.Shared.Return(Buffer);
}

/// <summary>Silero voice activity detector from sherpa-onnx. Not thread-safe.</summary>
internal sealed unsafe class VoiceActivityDetector : IDisposable
{
    public const int SampleRate = 16_000;

    private nint _handle;

    private VoiceActivityDetector(nint handle) => _handle = handle;

    public static VoiceActivityDetector CreateSilero(string modelPath, VadOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Silero VAD model not found: " + modelPath, modelPath);
        }

        using var strings = new NativeUtf8();
        SherpaOnnxVadModelConfig config = default;
        config.SileroVad.Model = strings.Add(modelPath);
        config.SileroVad.Threshold = options.Threshold;
        config.SileroVad.MinSilenceDuration = options.MinSilenceSeconds;
        config.SileroVad.MinSpeechDuration = options.MinSpeechSeconds;
        config.SileroVad.MaxSpeechDuration = options.MaxSpeechSeconds;
        config.SileroVad.WindowSize = options.WindowSize;
        config.SampleRate = SampleRate;
        config.NumThreads = 1;
        config.Provider = strings.Add("cpu");
        config.Debug = 0;

        var handle = SherpaOnnxNative.SherpaOnnxCreateVoiceActivityDetector(&config, options.BufferSeconds);
        if (handle == 0)
        {
            throw new InvalidOperationException($"sherpa-onnx could not create the Silero VAD from '{modelPath}' (see stderr for details).");
        }

        return new VoiceActivityDetector(handle);
    }

    public void AcceptWaveform(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        fixed (float* p = samples)
        {
            SherpaOnnxNative.SherpaOnnxVoiceActivityDetectorAcceptWaveform(_handle, p, samples.Length);
        }
    }

    /// <summary>Closes a segment that is still open at the end of the input.</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        SherpaOnnxNative.SherpaOnnxVoiceActivityDetectorFlush(_handle);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        SherpaOnnxNative.SherpaOnnxVoiceActivityDetectorReset(_handle);
    }

    /// <summary>Takes the oldest completed segment, copying its samples into an <see cref="ArrayPool{T}"/> buffer.</summary>
    public bool TryDequeue(out PooledSegment segment)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (SherpaOnnxNative.SherpaOnnxVoiceActivityDetectorEmpty(_handle) != 0)
        {
            segment = default;
            return false;
        }

        var s = SherpaOnnxNative.SherpaOnnxVoiceActivityDetectorFront(_handle);
        try
        {
            if (s is null)
            {
                segment = default;
                return false;
            }

            var n = Math.Max(s->N, 0);
            var buffer = ArrayPool<float>.Shared.Rent(Math.Max(n, 1));
            new ReadOnlySpan<float>(s->Samples, n).CopyTo(buffer);
            segment = new PooledSegment(s->Start, buffer, n);
            return true;
        }
        finally
        {
            if (s is not null)
            {
                SherpaOnnxNative.SherpaOnnxDestroySpeechSegment(s);
            }

            SherpaOnnxNative.SherpaOnnxVoiceActivityDetectorPop(_handle);
        }
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0)
        {
            SherpaOnnxNative.SherpaOnnxDestroyVoiceActivityDetector(h);
        }
    }
}
