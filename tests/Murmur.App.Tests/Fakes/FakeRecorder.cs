using Murmur.Core.Capture;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests.Fakes;

/// <summary>Scriptable <see cref="IRecorder"/>: no audio, deterministic state transitions, recorded calls.</summary>
internal sealed class FakeRecorder : IRecorder
{
    public RecorderState State { get; private set; }

    public SessionRecord? Current { get; private set; }

    public TimeSpan Elapsed { get; set; }

    public AudioLevels Levels { get; set; }

    public RecorderStats Stats { get; set; }

    /// <summary>The <see cref="SessionRecord.Duration"/> of the finished session when set; <see cref="Elapsed"/> otherwise.</summary>
    public TimeSpan? FinishedDuration { get; set; }

    public Exception? StartFailure { get; set; }

    public Exception? StopFailure { get; set; }

    public Exception? ScreenFailure { get; set; }

    public string? StopError { get; set; }

    /// <summary>
    /// When set, the start succeeds without the microphone and the session carries this warning in
    /// <see cref="SessionRecord.Error"/>, like <see cref="Recorder"/> when Windows denies microphone access.
    /// </summary>
    public string? StartWarning { get; set; }

    /// <summary>Screen segments the finished session carries (<see cref="SessionRecord.ScreenSegments"/>).</summary>
    public List<ScreenSegment> Segments { get; } = [];

    /// <summary>Runs inside <see cref="StartAsync"/> once the recorder is recording (e.g. to raise warnings).</summary>
    public Action? AfterStart { get; set; }

    public List<RecordingOptions> Starts { get; } = [];

    public List<bool> ScreenCalls { get; } = [];

    /// <summary>Number of <see cref="StopAsync"/> calls, failed ones included.</summary>
    public int Stops { get; private set; }

    public event Action<RecorderState>? StateChanged;

    public event Action<RecorderWarning>? Warning;

    /// <summary>A screen segment is running: switched on (at the start or later) and not ended by <see cref="FailScreen"/>.</summary>
    public bool IsScreenRecording { get; set; }

    /// <summary>Simulates a fatal screen fault: the segment ends, the audio goes on, <see cref="Warning"/> is raised.</summary>
    public void FailScreen(string message = "Запис екрана зупинився через помилку; звук записується далі.")
    {
        IsScreenRecording = false;
        Warning?.Invoke(new RecorderWarning("screen", message, ScreenStopped: true));
    }

    /// <summary>Raises a non-fatal warning.</summary>
    public void RaiseWarning(string message, CaptureFaultKind kind = CaptureFaultKind.Other) => Warning?.Invoke(new RecorderWarning("screen", message, ScreenStopped: false, kind));

    public ValueTask<SessionRecord> StartAsync(RecordingOptions options, CancellationToken cancellationToken)
    {
        Starts.Add(options);
        if (StartFailure is { } failure)
        {
            throw failure;
        }

        SetState(RecorderState.Starting);
        Current = new SessionRecord
        {
            Id = SessionId.New(),
            Title = options.Title ?? "Fake",
            StartedAt = DateTimeOffset.UnixEpoch,
            State = SessionState.Recording,
            HasMicrophone = options.Microphone && StartWarning is null,
            HasSystemAudio = options.SystemAudio,
            Error = StartWarning,
        };
        IsScreenRecording = options.Screen;
        SetState(RecorderState.Recording);
        AfterStart?.Invoke();
        return ValueTask.FromResult(Current);
    }

    public ValueTask SetScreenEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        ScreenCalls.Add(enabled);
        if (ScreenFailure is { } failure)
        {
            throw failure;
        }

        IsScreenRecording = enabled;
        return ValueTask.CompletedTask;
    }

    /// <summary>When set, <see cref="StopAsync"/> enters Stopping and then waits for this task before finishing.</summary>
    public Task? StopGate { get; set; }

    /// <remarks>
    /// Completes synchronously unless <see cref="StopGate"/> is set. <see cref="StopFailure"/> is thrown synchronously
    /// before any state change (the fake keeps recording). Like <see cref="Recorder"/>, stopping when not recording
    /// changes nothing and fails through the returned task.
    /// </remarks>
    public ValueTask<SessionRecord> StopAsync(CancellationToken cancellationToken)
    {
        Stops++;
        if (State != RecorderState.Recording || Current is not { } current)
        {
            return ValueTask.FromException<SessionRecord>(new InvalidOperationException("Not recording."));
        }

        if (StopFailure is { } failure)
        {
            throw failure;
        }

        SetState(RecorderState.Stopping);
        return StopGate is { } gate ? FinishAfterAsync(gate, current) : ValueTask.FromResult(Finish(current));
    }

    private async ValueTask<SessionRecord> FinishAfterAsync(Task gate, SessionRecord current)
    {
        await gate;
        return Finish(current);
    }

    private SessionRecord Finish(SessionRecord current)
    {
        var finished = current with
        {
            State = SessionState.Recorded,
            Duration = FinishedDuration ?? Elapsed,
            ScreenSegments = [.. Segments],
            Error = StopError ?? current.Error,
        };
        Current = null;
        IsScreenRecording = false;
        SetState(RecorderState.Idle);
        return finished;
    }

    /// <summary>Simulates the recorder ending the session by itself (every audio source failed).</summary>
    public void FailBySelf()
    {
        Current = null;
        IsScreenRecording = false;
        SetState(RecorderState.Idle);
    }

    /// <summary>Puts the fake straight into the recording state (for screenshots and layout tests).</summary>
    public void ForceRecording(SessionRecord session)
    {
        Current = session;
        SetState(RecorderState.Recording);
    }

    private void SetState(RecorderState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}
