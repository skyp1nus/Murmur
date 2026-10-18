using System.Buffers;
using System.Collections.Concurrent;
using Murmur.Core.Transcription.Protocol;
using Murmur.Transcription.Native;
using Murmur.Transcription.Pipeline;

namespace Murmur.Transcription.Tests;

/// <summary>Energy VAD: non-zero samples are speech; a run of <c>silence</c> zeros closes a segment.</summary>
internal sealed class FakeVad(int silence = 1_600) : IVoiceActivityDetector
{
    private readonly Queue<PooledSegment> _ready = new();
    private readonly List<float> _current = [];
    private long _position;
    private long _start = -1;
    private int _zeros;

    public List<int> CallSizes { get; } = [];

    public bool Disposed { get; private set; }

    public void AcceptWaveform(ReadOnlySpan<float> samples)
    {
        CallSizes.Add(samples.Length);
        foreach (var s in samples)
        {
            if (s != 0f)
            {
                if (_start < 0)
                {
                    _start = _position;
                }

                _current.Add(s);
                _zeros = 0;
            }
            else if (_start >= 0)
            {
                _zeros++;
                _current.Add(s);
                if (_zeros >= silence)
                {
                    Close();
                }
            }

            _position++;
        }
    }

    public void Flush() => Close();

    private void Close()
    {
        if (_start >= 0)
        {
            var trimmed = _current.Count - Math.Min(_zeros, _current.Count);
            var buffer = ArrayPool<float>.Shared.Rent(Math.Max(1, trimmed));
            _current.CopyTo(0, buffer, 0, trimmed);
            _ready.Enqueue(new PooledSegment(_start, buffer, trimmed));
        }

        _current.Clear();
        _start = -1;
        _zeros = 0;
    }

    public bool TryDequeue(out PooledSegment segment) => _ready.TryDequeue(out segment);

    public void Dispose() => Disposed = true;
}

/// <summary>
/// "Recognizes" a segment as the text <c>seg{first sample * 1000}</c> with one token per 0.5 s, after a delay that
/// varies per call so parallel workers finish out of order.
/// </summary>
internal sealed class FakeRecognizer : ISpeechRecognizer
{
    private int _active;
    private int _calls;

    public int MaxConcurrency { get; private set; }

    public int Calls => _calls;

    public ConcurrentBag<int> BatchSizes { get; } = [];

    public Func<int, int> DelayMs { get; init; } = call => (call * 7919) % 23;

    public Func<int, Exception?> FailOnCall { get; init; } = _ => null;

    public void Decode(ReadOnlySpan<ReadOnlyMemory<float>> utterances, Span<RecognitionResult> results)
    {
        var call = Interlocked.Increment(ref _calls);
        var active = Interlocked.Increment(ref _active);
        lock (BatchSizes)
        {
            MaxConcurrency = Math.Max(MaxConcurrency, active);
        }

        BatchSizes.Add(utterances.Length);
        try
        {
            Thread.Sleep(DelayMs(call));
            if (FailOnCall(call) is { } ex)
            {
                throw ex;
            }

            for (var i = 0; i < utterances.Length; i++)
            {
                var span = utterances[i].Span;
                var seconds = span.Length / 16_000f;
                var n = Math.Max(1, (int)(seconds / 0.5f));
                var tokens = new string[n];
                var times = new float[n];
                var durations = new float[n];
                for (var t = 0; t < n; t++)
                {
                    tokens[t] = t == 0 ? $"▁seg{MathF.Round(span[0] * 1000)}" : "▁w" + t;
                    times[t] = t * 0.5f;
                    durations[t] = 0.25f;
                }

                results[i] = new RecognitionResult(string.Join(' ', tokens).Replace("▁", string.Empty, StringComparison.Ordinal), tokens, times, durations, "en");
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

/// <summary>Engine for in-process server tests: emits scripted segments, honours cancellation between "batches".</summary>
internal sealed class FakeEngine : ITranscriptionEngine
{
    public int Batches { get; init; } = 3;

    public TimeSpan BatchDelay { get; init; } = TimeSpan.FromMilliseconds(5);

    public ManualResetEventSlim? Gate { get; init; }

    public ConcurrentQueue<string> Jobs { get; } = new();

    public bool Disposed { get; private set; }

    public PipelineStats Transcribe(string audioPath, Action<WorkerSegment> onSegment, Action<double, double>? onProgress, CancellationToken cancellationToken)
    {
        Jobs.Enqueue(audioPath);
        if (audioPath.EndsWith("bad.wav", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unsupported WAV format: 44100 Hz, 2 ch, PCM16.");
        }

        for (var b = 0; b < Batches; b++)
        {
            Gate?.Wait(cancellationToken);
            Thread.Sleep(BatchDelay);
            cancellationToken.ThrowIfCancellationRequested();
            onSegment(new WorkerSegment(b, b + 0.5, "text " + b, [new WorkerWord("text", b, b + 0.25)]));
            onProgress?.Invoke(b + 1, Batches);
        }

        return new PipelineStats(Batches, Batches, Batches, Batches);
    }

    public void Dispose() => Disposed = true;
}
