using BenchmarkDotNet.Attributes;
using Murmur.Core.Activity;
using Murmur.Core.Models;
using Murmur.Core.Processing;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.Benchmarks;

/// <summary>
/// The paths a fast source takes into the activity feed: a download reporting progress and a transcription reporting progress. Both
/// must stay allocation-free (the feed only stores the fact and arms its coalescing timer), however often they fire.
/// </summary>
[MemoryDiagnoser]
public class ActivityFeedBenchmarks
{
    private ActivityFeed _feed = null!;
    private ModelStatus _download = null!;
    private SessionProcessingStatus _transcribing = null!;

    [GlobalSetup]
    public void Setup()
    {
        _feed = new ActivityFeed(new IdleRecorder());
        _download = new ModelStatus(new ModelInfo("gemma", ModelKind.Chat, "Gemma 4 26B", 1_000_000, false), ModelInstallState.Downloading, 420_000, 1_000_000, null);
        _transcribing = new SessionProcessingStatus(SessionId.New(), SessionProcessingStage.Transcribing, 0.64);
        _feed.OnModelStatus(_download);
        _feed.OnProcessing(_transcribing);
    }

    [GlobalCleanup]
    public void Cleanup() => _feed.Dispose();

    [Benchmark]
    public void ModelProgress() => _feed.OnModelStatus(_download);

    [Benchmark]
    public void TranscriptionProgress() => _feed.OnProcessing(_transcribing);

    private sealed class IdleRecorder : IRecorder
    {
        public RecorderState State => RecorderState.Idle;

        public SessionRecord? Current => null;

        public TimeSpan Elapsed => TimeSpan.Zero;

        public Murmur.Core.Recording.AudioLevels Levels => default;

        public RecorderStats Stats => default;

        public bool IsScreenRecording => false;

        public event Action<RecorderState>? StateChanged
        {
            add { }
            remove { }
        }

        public event Action<RecorderWarning>? Warning
        {
            add { }
            remove { }
        }

        public ValueTask<SessionRecord> StartAsync(RecordingOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SetScreenEnabledAsync(bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask PauseAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ResumeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SessionRecord> StopAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
