using System.Buffers;
using Murmur.Transcription.Audio;
using Murmur.Transcription.Native;

namespace Murmur.Transcription.Pipeline;

/// <summary>What the pipeline needs from a VAD; implemented by the Silero <see cref="VoiceActivityDetector"/>.</summary>
internal interface IVoiceActivityDetector : IDisposable
{
    void AcceptWaveform(ReadOnlySpan<float> samples);

    void Flush();

    bool TryDequeue(out PooledSegment segment);
}

/// <summary>Adapts the native detector to <see cref="IVoiceActivityDetector"/>.</summary>
internal sealed class SileroVad(VoiceActivityDetector detector) : IVoiceActivityDetector
{
    public void AcceptWaveform(ReadOnlySpan<float> samples) => detector.AcceptWaveform(samples);

    public void Flush() => detector.Flush();

    public bool TryDequeue(out PooledSegment segment) => detector.TryDequeue(out segment);

    public void Dispose() => detector.Dispose();
}

/// <summary>
/// Streams a source through the VAD in fixed-size blocks and hands out speech segments in time order. Segments longer
/// than <see cref="PipelineOptions.MaxSegmentSeconds"/> (the VAD only "encourages" a split) are cut into equal parts of
/// at most <see cref="PipelineOptions.SplitSeconds"/>.
/// </summary>
internal static class SpeechSegmenter
{
    public const int SampleRate = 16_000;

    /// <param name="emit">Receives ownership of each segment's pooled buffer.</param>
    /// <param name="onRead">Called with the number of samples consumed so far after each block.</param>
    public static void Run(
        ISampleSource source,
        IVoiceActivityDetector vad,
        PipelineOptions options,
        Action<PooledSegment> emit,
        Action<long>? onRead,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(vad);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(emit);
        if (source.TotalSamples > int.MaxValue)
        {
            // The VAD reports segment starts as int32 sample indices (~37 h at 16 kHz).
            throw new InvalidDataException("Audio longer than 37 hours is not supported.");
        }

        var window = Math.Max(1, options.Vad.WindowSize);
        var block = ArrayPool<float>.Shared.Rent(options.ReadBlockSamples);
        try
        {
            long consumed = 0;
            int n;
            while ((n = source.Read(block.AsSpan(0, options.ReadBlockSamples))) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // One VAD window per call: sherpa-onnx ORs the speech flags of all windows in a call and places the
                // segment start relative to the end of the call, so bigger calls merge phrases and clip their onsets.
                for (var offset = 0; offset < n; offset += window)
                {
                    vad.AcceptWaveform(block.AsSpan(offset, Math.Min(window, n - offset)));
                }

                consumed += n;
                Drain(vad, options, emit);
                onRead?.Invoke(consumed);
            }

            vad.Flush();
            Drain(vad, options, emit);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(block);
        }
    }

    private static void Drain(IVoiceActivityDetector vad, PipelineOptions options, Action<PooledSegment> emit)
    {
        var max = (int)(options.MaxSegmentSeconds * SampleRate);
        while (vad.TryDequeue(out var segment))
        {
            if (segment.Length == 0)
            {
                segment.Release();
                continue;
            }

            if (segment.Length <= max)
            {
                emit(segment);
                continue;
            }

            Split(segment, (int)(options.SplitSeconds * SampleRate), emit);
        }
    }

    /// <summary>Cuts <paramref name="segment"/> into ceil(length / target) parts of equal length; releases the original.</summary>
    internal static void Split(PooledSegment segment, int targetSamples, Action<PooledSegment> emit)
    {
        try
        {
            var parts = (segment.Length + targetSamples - 1) / targetSamples;
            var offset = 0;
            for (var p = 0; p < parts; p++)
            {
                var length = (segment.Length - offset) / (parts - p);
                var buffer = ArrayPool<float>.Shared.Rent(length);
                segment.Buffer.AsSpan(offset, length).CopyTo(buffer);
                emit(new PooledSegment(segment.StartSample + offset, buffer, length));
                offset += length;
            }
        }
        finally
        {
            segment.Release();
        }
    }
}
