using System.Collections.Concurrent;
using Murmur.Core.Transcription.Protocol;
using Murmur.Transcription.Audio;
using Murmur.Transcription.Native;

namespace Murmur.Transcription.Pipeline;

internal sealed record PipelineOptions
{
    /// <summary>
    /// Segments per DecodeMultipleOfflineStreams call. Measured on 4 vCPU: batching padded Parakeet inputs is slower
    /// than single-utterance calls run in parallel (see <see cref="Parallelism"/>), so 1 is the default.
    /// </summary>
    public int BatchSize { get; init; } = 1;

    /// <summary>
    /// Decode workers sharing the one recognizer (shared weights: no extra model memory). Each runs its own
    /// DecodeMultipleOfflineStreams calls; results are re-ordered so segments are still emitted in time order.
    /// </summary>
    public int Parallelism { get; init; } = 1;

    /// <summary>VAD segments longer than this are split (Parakeet quality drops on very long inputs).</summary>
    public float MaxSegmentSeconds { get; init; } = 20f;

    /// <summary>Target length of the parts of a split segment.</summary>
    public float SplitSeconds { get; init; } = 15f;

    /// <summary>Samples read from the source per step (~1 s); fed to the VAD one window at a time.</summary>
    public int ReadBlockSamples { get; init; } = 16 * 1024;

    public VadOptions Vad { get; init; } = new();
}

/// <summary>What the pipeline needs from an ASR engine; implemented by <see cref="OfflineRecognizer"/>.</summary>
internal interface ISpeechRecognizer
{
    /// <summary>Must be safe to call from several threads at once (sherpa-onnx / ONNX Runtime sessions are).</summary>
    void Decode(ReadOnlySpan<ReadOnlyMemory<float>> utterances, Span<RecognitionResult> results);
}

internal sealed class NativeSpeechRecognizer(OfflineRecognizer recognizer) : ISpeechRecognizer
{
    public void Decode(ReadOnlySpan<ReadOnlyMemory<float>> utterances, Span<RecognitionResult> results) =>
        recognizer.Decode(utterances, results);
}

internal readonly record struct PipelineStats(double AudioSeconds, int Segments, int Words, int Batches);

/// <summary>
/// Source -> Silero VAD (own thread) -> bounded queue -> N decode workers (batches of <see cref="PipelineOptions.BatchSize"/>)
/// -> re-order buffer -> segments with word times, emitted in time order on the calling thread.
/// Cancellation is honoured between batches: workers finish their current batch, nothing further is emitted.
/// </summary>
internal sealed class TranscriptionPipeline(ISpeechRecognizer recognizer, Func<IVoiceActivityDetector> createVad, PipelineOptions options)
{
    public const double SampleRate = 16_000.0;

    public PipelineOptions Options { get; } = options;

    /// <param name="onSegment">Non-empty segments in time order.</param>
    /// <param name="onProgress">(processed seconds, total seconds) after each decoded batch, and once at the end.</param>
    public PipelineStats Run(
        ISampleSource source,
        Action<WorkerSegment> onSegment,
        Action<double, double>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onSegment);
        cancellationToken.ThrowIfCancellationRequested();

        var run = new PipelineRun(recognizer, createVad(), Options, source, cancellationToken);
        try
        {
            return run.Execute(onSegment, onProgress);
        }
        finally
        {
            run.Dispose();
        }
    }

    private readonly record struct Item(long Sequence, PooledSegment Segment);

    /// <summary>State of one <see cref="TranscriptionPipeline.Run"/> call.</summary>
    private sealed class PipelineRun : IDisposable
    {
        private readonly ISpeechRecognizer _recognizer;
        private readonly IVoiceActivityDetector _vad;
        private readonly PipelineOptions _options;
        private readonly ISampleSource _source;
        private readonly CancellationToken _cancellation;
        private readonly CancellationTokenSource _stop;
        private readonly BlockingCollection<Item> _queue;
        private readonly object _gate = new();
        private readonly Dictionary<long, (PooledSegment Segment, RecognitionResult Result)> _decoded = [];
        private readonly Thread _producer;
        private readonly Thread[] _workers;
        private readonly int _batchSize;
        private Exception? _error;
        private int _finishedWorkers;
        private int _batches;

        public PipelineRun(ISpeechRecognizer recognizer, IVoiceActivityDetector vad, PipelineOptions options, ISampleSource source, CancellationToken cancellation)
        {
            _recognizer = recognizer;
            _vad = vad;
            _options = options;
            _source = source;
            _cancellation = cancellation;
            _batchSize = Math.Max(1, options.BatchSize);
            var workers = Math.Max(1, options.Parallelism);
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            _queue = new BlockingCollection<Item>(boundedCapacity: (_batchSize * workers * 2) + 2);
            _producer = new Thread(Produce) { IsBackground = true, Name = "murmur-vad" };
            _workers = new Thread[workers];
            for (var i = 0; i < workers; i++)
            {
                _workers[i] = new Thread(Work) { IsBackground = true, Name = "murmur-asr-" + i };
            }
        }

        private int WorkerCount => _workers.Length;

        public PipelineStats Execute(Action<WorkerSegment> onSegment, Action<double, double>? onProgress)
        {
            var total = _source.TotalSamples / SampleRate;
            using var wake = _cancellation.Register(static g =>
            {
                lock (g!)
                {
                    Monitor.PulseAll(g);
                }
            }, _gate);

            _producer.Start();
            foreach (var w in _workers)
            {
                w.Start();
            }

            var ready = new List<(PooledSegment Segment, RecognitionResult Result)>();
            long next = 0;
            int segments = 0, words = 0;
            var processed = 0.0;

            while (true)
            {
                bool finished;
                lock (_gate)
                {
                    while (!_decoded.ContainsKey(next) && _error is null && _finishedWorkers < WorkerCount && !_cancellation.IsCancellationRequested)
                    {
                        Monitor.Wait(_gate);
                    }

                    _cancellation.ThrowIfCancellationRequested();
                    ThrowIfFailed();
                    while (_decoded.Remove(next, out var entry))
                    {
                        ready.Add(entry);
                        next++;
                    }

                    finished = ready.Count == 0 && _finishedWorkers == WorkerCount;
                }

                if (finished)
                {
                    break;
                }

                try
                {
                    foreach (var (segment, r) in ready)
                    {
                        _cancellation.ThrowIfCancellationRequested();
                        var start = segment.StartSample / SampleRate;
                        var end = (segment.StartSample + segment.Length) / SampleRate;
                        processed = Math.Max(processed, end);
                        if (r.Text.Length == 0)
                        {
                            continue;
                        }

                        var w = WordBuilder.Build(r.Tokens, r.Timestamps, r.Durations, start, end);
                        onSegment(new WorkerSegment(WordBuilder.Round(start), WordBuilder.Round(end), r.Text, w));
                        segments++;
                        words += w.Count;
                    }
                }
                finally
                {
                    foreach (var (segment, _) in ready)
                    {
                        segment.Release();
                    }

                    ready.Clear();
                }

                onProgress?.Invoke(WordBuilder.Round(Math.Min(processed, total)), WordBuilder.Round(total));
            }

            lock (_gate)
            {
                ThrowIfFailed();
                if (_decoded.Count > 0)
                {
                    throw new InvalidOperationException($"Internal error: {_decoded.Count} decoded segment(s) were never emitted.");
                }
            }

            _cancellation.ThrowIfCancellationRequested();
            onProgress?.Invoke(WordBuilder.Round(total), WordBuilder.Round(total));
            return new PipelineStats(total, segments, words, Volatile.Read(ref _batches));
        }

        private void ThrowIfFailed()
        {
            if (_error is InvalidDataException data)
            {
                throw new InvalidDataException(data.Message, data);
            }

            if (_error is not null)
            {
                throw new InvalidOperationException("Transcription failed: " + _error.Message, _error);
            }
        }

        private void Produce()
        {
            try
            {
                long sequence = 0;
                SpeechSegmenter.Run(_source, _vad, _options, s =>
                {
                    try
                    {
                        _queue.Add(new Item(sequence, s), _stop.Token);
                        sequence++;
                    }
                    catch
                    {
                        s.Release();
                        throw;
                    }
                }, null, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
            finally
            {
                _queue.CompleteAdding();
            }
        }

        private void Work()
        {
            var items = new Item[_batchSize];
            var inputs = new ReadOnlyMemory<float>[_batchSize];
            var results = new RecognitionResult[_batchSize];
            var count = 0;
            try
            {
                while (true)
                {
                    if (!_queue.TryTake(out var first, Timeout.Infinite, _stop.Token))
                    {
                        break; // producer finished and the queue is drained
                    }

                    items[count++] = first;
                    while (count < _batchSize && _queue.TryTake(out var more))
                    {
                        items[count++] = more;
                    }

                    for (var i = 0; i < count; i++)
                    {
                        inputs[i] = items[i].Segment.Samples;
                    }

                    _recognizer.Decode(inputs.AsSpan(0, count), results.AsSpan(0, count));
                    Interlocked.Increment(ref _batches);

                    lock (_gate)
                    {
                        for (var i = 0; i < count; i++)
                        {
                            _decoded.Add(items[i].Sequence, (items[i].Segment, results[i]));
                        }

                        count = 0;
                        Monitor.PulseAll(_gate);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
            finally
            {
                for (var i = 0; i < count; i++)
                {
                    items[i].Segment.Release();
                }

                lock (_gate)
                {
                    _finishedWorkers++;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        private void Fail(Exception ex)
        {
            lock (_gate)
            {
                _error ??= ex;
                Monitor.PulseAll(_gate);
            }

            _stop.Cancel();
        }

        public void Dispose()
        {
            _stop.Cancel();
            if ((_producer.ThreadState & ThreadState.Unstarted) == 0)
            {
                _producer.Join();
            }

            foreach (var w in _workers)
            {
                if ((w.ThreadState & ThreadState.Unstarted) == 0)
                {
                    w.Join();
                }
            }

            _vad.Dispose();
            while (_queue.TryTake(out var left))
            {
                left.Segment.Release();
            }

            foreach (var (segment, _) in _decoded.Values)
            {
                segment.Release();
            }

            _decoded.Clear();
            _queue.Dispose();
            _stop.Dispose();
        }
    }
}
