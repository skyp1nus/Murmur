using System.Runtime.CompilerServices;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Core.Tests.Fakes;

/// <summary>In-memory <see cref="ITranscriptStore"/>.</summary>
internal sealed class InMemoryTranscriptStore : ITranscriptStore
{
    private readonly Dictionary<SessionId, Transcript> _transcripts = [];

    public int Saves { get; private set; }

    public Exception? FailWith { get; set; }

    public ValueTask SaveTranscriptAsync(Transcript transcript, CancellationToken cancellationToken)
    {
        if (FailWith is { } error)
        {
            throw error;
        }

        lock (_transcripts)
        {
            _transcripts[transcript.Session] = transcript;
            Saves++;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<Transcript?> GetTranscriptAsync(SessionId id, CancellationToken cancellationToken)
    {
        lock (_transcripts)
        {
            return ValueTask.FromResult(_transcripts.GetValueOrDefault(id));
        }
    }
}

/// <summary>
/// Scriptable in-process <see cref="IAsr"/>: per audio file name, a list of (start, end, text) segments.
/// Optionally blocks each request on <see cref="Gate"/> so tests can observe the running state.
/// </summary>
internal sealed class FakeAsr : IAsr
{
    private readonly Dictionary<string, (double Start, double End, string Text)[]> _script = new(StringComparer.Ordinal);
    private readonly List<AsrRequest> _requests = [];
    private int _busy;

    public bool Installed { get; set; } = true;

    /// <summary>When set, each request waits for it before emitting (cancellable).</summary>
    public SemaphoreSlim? Gate { get; set; }

    /// <summary>Thrown after the first segment of a request for this file name.</summary>
    public string? FailOn { get; set; }

    public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AsrStatus Status => !Installed ? AsrStatus.ModelMissing : Volatile.Read(ref _busy) != 0 ? AsrStatus.Busy : AsrStatus.Idle;

    public IReadOnlyList<AsrRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public void Script(string fileName, params (double Start, double End, string Text)[] segments) => _script[fileName] = segments;

    public void ResetStarted() => Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(AsrRequest request, IProgress<AsrProgress>? progress, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        Interlocked.Exchange(ref _busy, 1);
        try
        {
            Started.TrySetResult();
            if (Gate is { } gate)
            {
                await gate.WaitAsync(cancellationToken);
            }

            var name = Path.GetFileName(request.AudioPath);
            var segments = _script.GetValueOrDefault(name, []);
            var total = segments.Length == 0 ? 1 : segments[^1].End;
            foreach (var (start, end, text) in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new TranscriptSegment(request.Speaker, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, [new TranscriptWord(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end))]);
                progress?.Report(new AsrProgress(TimeSpan.FromSeconds(end), TimeSpan.FromSeconds(total)));
                if (FailOn == name)
                {
                    throw new AsrWorkerException("simulated ASR failure");
                }
            }

            await Task.Yield();
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }
}

/// <summary><see cref="IRecorder"/> whose state is driven by the test.</summary>
internal sealed class FakeRecorder : IRecorder
{
    public RecorderState State { get; private set; } = RecorderState.Idle;

    public SessionRecord? Current { get; private set; }

    public TimeSpan Elapsed => TimeSpan.Zero;

    public AudioLevels Levels => default;

    public RecorderStats Stats => default;

    public event Action<RecorderState>? StateChanged;

    /// <summary>Raises Starting -&gt; Recording for <paramref name="session"/> (Current is set from Recording on, like the real recorder).</summary>
    public void BeginRecording(SessionRecord session)
    {
        Set(RecorderState.Starting);
        Current = session;
        Set(RecorderState.Recording);
    }

    /// <summary>Raises Stopping -&gt; Idle; the session row must already be updated by the test.</summary>
    public void EndRecording()
    {
        Set(RecorderState.Stopping);
        Current = null;
        Set(RecorderState.Idle);
    }

    public ValueTask<SessionRecord> StartAsync(RecordingOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask SetScreenEnabledAsync(bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<SessionRecord> StopAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    private void Set(RecorderState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}
