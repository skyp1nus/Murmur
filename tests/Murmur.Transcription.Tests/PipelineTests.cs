using System.Buffers;
using Murmur.Core.Transcription.Protocol;
using Murmur.Transcription.Audio;
using Murmur.Transcription.Native;
using Murmur.Transcription.Pipeline;

namespace Murmur.Transcription.Tests;

public sealed class PipelineTests
{
    /// <summary>n "utterances" of constant value (i+1)/1000, each <paramref name="speech"/> samples, separated by 1 s of zeros.</summary>
    private static ConcatenatedLikeSource Utterances(int n, int speech = 16_000)
    {
        var data = new List<float>();
        for (var i = 0; i < n; i++)
        {
            data.AddRange(Enumerable.Repeat(0f, 16_000));
            data.AddRange(Enumerable.Repeat((i + 1) / 1000f, speech));
        }

        data.AddRange(Enumerable.Repeat(0f, 8_000));
        return new ConcatenatedLikeSource([.. data]);
    }

    private static TranscriptionPipeline Pipeline(ISpeechRecognizer recognizer, PipelineOptions options, FakeVad? vad = null) =>
        new(recognizer, () => vad ?? new FakeVad(), options);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 4)]
    [InlineData(3, 1)]
    [InlineData(3, 4)]
    [InlineData(8, 2)]
    public void Segments_AreEmittedInTimeOrder_ForAnyBatchAndParallelism(int batch, int parallel)
    {
        var recognizer = new FakeRecognizer();
        var segments = new List<WorkerSegment>();
        var progress = new List<(double, double)>();

        var stats = Pipeline(recognizer, new PipelineOptions { BatchSize = batch, Parallelism = parallel })
            .Run(Utterances(25), segments.Add, (p, t) => progress.Add((p, t)), CancellationToken.None);

        Assert.Equal(25, segments.Count);
        Assert.Equal(25, stats.Segments);
        for (var i = 0; i < segments.Count; i++)
        {
            Assert.StartsWith($"seg{i + 1} ", segments[i].Text + " ", StringComparison.Ordinal);
            Assert.Equal((i * 2) + 1, segments[i].Start, 3);
            Assert.Equal((i * 2) + 2, segments[i].End, 3);
            Assert.All(segments[i].Words, w => Assert.InRange(w.Start, segments[i].Start, segments[i].End));
        }

        Assert.All(recognizer.BatchSizes, b => Assert.InRange(b, 1, batch));
        Assert.True(recognizer.MaxConcurrency <= parallel);
        Assert.Equal(stats.Batches, recognizer.Calls);

        // progress: monotonic, ends at total
        Assert.Equal(progress.OrderBy(p => p.Item1).ToList(), progress);
        Assert.Equal(stats.AudioSeconds, progress[^1].Item1);
        Assert.Equal(stats.AudioSeconds, progress[^1].Item2);
    }

    [Fact]
    public void ParallelWorkers_ActuallyOverlap()
    {
        var recognizer = new FakeRecognizer { DelayMs = _ => 30 };

        Pipeline(recognizer, new PipelineOptions { Parallelism = 4 }).Run(Utterances(16), _ => { }, null, CancellationToken.None);

        Assert.True(recognizer.MaxConcurrency > 1, $"max concurrency {recognizer.MaxConcurrency}");
    }

    [Fact]
    public void Vad_IsFedOneWindowPerCall()
    {
        var vad = new FakeVad();

        Pipeline(new FakeRecognizer(), new PipelineOptions(), vad).Run(Utterances(2), _ => { }, null, CancellationToken.None);

        Assert.All(vad.CallSizes[..^1], n => Assert.Equal(512, n));
        Assert.InRange(vad.CallSizes[^1], 1, 512);
        Assert.True(vad.Disposed);
    }

    [Fact]
    public void LongSegments_AreSplitIntoEqualPartsNoLongerThanTheTarget()
    {
        var segments = new List<WorkerSegment>();
        var options = new PipelineOptions { MaxSegmentSeconds = 20, SplitSeconds = 15 };

        Pipeline(new FakeRecognizer(), options).Run(Utterances(1, speech: 16_000 * 50), segments.Add, null, CancellationToken.None);

        Assert.Equal(4, segments.Count); // ceil(50 / 15)
        Assert.All(segments, s => Assert.Equal(12.5, s.End - s.Start, 3));
        Assert.Equal(1.0, segments[0].Start, 3);
        Assert.Equal(51.0, segments[^1].End, 3);
    }

    [Fact]
    public void Split_ProducesContiguousParts_AndReturnsTheOriginalBuffer()
    {
        var buffer = ArrayPool<float>.Shared.Rent(1000);
        for (var i = 0; i < 1000; i++)
        {
            buffer[i] = i;
        }

        var parts = new List<PooledSegment>();
        SpeechSegmenter.Split(new PooledSegment(5_000, buffer, 1000), 300, parts.Add);

        Assert.Equal([250, 250, 250, 250], parts.Select(p => p.Length));
        Assert.Equal([5_000L, 5_250, 5_500, 5_750], parts.Select(p => p.StartSample));
        Assert.Equal(750f, parts[3].Samples.Span[0]);
        parts.ForEach(p => p.Release());
    }

    [Fact]
    public void EmptyRecognitionResults_AreNotEmitted()
    {
        var segments = new List<WorkerSegment>();
        var silent = new SilentRecognizer();

        var stats = Pipeline(silent, new PipelineOptions()).Run(Utterances(3), segments.Add, null, CancellationToken.None);

        Assert.Empty(segments);
        Assert.Equal(0, stats.Segments);
        Assert.Equal(3, silent.Utterances);
    }

    [Fact]
    public void SilenceOnly_CompletesWithFinalProgress()
    {
        var progress = new List<(double, double)>();

        var stats = Pipeline(new FakeRecognizer(), new PipelineOptions())
            .Run(new ConcatenatedLikeSource(new float[32_000]), _ => { }, (p, t) => progress.Add((p, t)), CancellationToken.None);

        Assert.Equal(2.0, stats.AudioSeconds);
        Assert.Equal([(2.0, 2.0)], progress);
    }

    [Fact]
    public void Cancellation_StopsBetweenBatches_AndThrows()
    {
        using var cts = new CancellationTokenSource();
        var segments = new List<WorkerSegment>();
        var recognizer = new FakeRecognizer { DelayMs = _ => 20 };

        Assert.ThrowsAny<OperationCanceledException>(() =>
            Pipeline(recognizer, new PipelineOptions { Parallelism = 2 }).Run(Utterances(200), s =>
            {
                segments.Add(s);
                if (segments.Count == 3)
                {
                    cts.Cancel();
                }
            }, null, cts.Token));

        Assert.Equal(3, segments.Count);
        Assert.True(recognizer.Calls < 20, $"{recognizer.Calls} decode calls after cancel");
    }

    [Fact]
    public void AlreadyCancelledToken_ThrowsBeforeAnyWork()
    {
        var recognizer = new FakeRecognizer();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            Pipeline(recognizer, new PipelineOptions()).Run(Utterances(2), _ => { }, null, new CancellationToken(canceled: true)));
        Assert.Equal(0, recognizer.Calls);
    }

    [Fact]
    public void RecognizerFailure_PropagatesAsInvalidOperation()
    {
        var recognizer = new FakeRecognizer { FailOnCall = c => c == 2 ? new InvalidOperationException("boom") : null };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Pipeline(recognizer, new PipelineOptions { Parallelism = 3 }).Run(Utterances(10), _ => { }, null, CancellationToken.None));

        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceFailure_Propagates()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            Pipeline(new FakeRecognizer(), new PipelineOptions()).Run(new FailingSource(), _ => { }, null, CancellationToken.None));

        Assert.Contains("disk on fire", ex.Message, StringComparison.Ordinal);
    }

    private sealed class SilentRecognizer : ISpeechRecognizer
    {
        private int _utterances;

        public int Utterances => _utterances;

        public void Decode(ReadOnlySpan<ReadOnlyMemory<float>> utterances, Span<RecognitionResult> results)
        {
            Interlocked.Add(ref _utterances, utterances.Length);
            for (var i = 0; i < utterances.Length; i++)
            {
                results[i] = RecognitionResult.Empty;
            }
        }
    }

    private sealed class FailingSource : ISampleSource
    {
        private int _reads;

        public long TotalSamples => 1_000_000;

        public int Read(Span<float> destination)
        {
            if (++_reads > 3)
            {
                throw new InvalidDataException("disk on fire");
            }

            destination.Fill(0.1f);
            return destination.Length;
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>In-memory 16 kHz source.</summary>
internal sealed class ConcatenatedLikeSource(float[] samples) : ISampleSource
{
    private int _position;

    public long TotalSamples => samples.Length;

    public int Read(Span<float> destination)
    {
        var n = Math.Min(destination.Length, samples.Length - _position);
        samples.AsSpan(_position, n).CopyTo(destination);
        _position += n;
        return n;
    }

    public void Dispose()
    {
    }
}
