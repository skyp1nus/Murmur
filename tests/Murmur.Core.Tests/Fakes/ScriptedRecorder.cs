using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.Core.Tests.Fakes;

/// <summary>
/// An <see cref="IRecorder"/> the test drives by hand: the state, <see cref="Elapsed"/> and the session are set directly, the way
/// <see cref="Recorder"/> would report them (Idle, Starting, Recording, Paused, Stopping, Idle). Pause freezes <see cref="Elapsed"/>.
/// </summary>
internal sealed class ScriptedRecorder : IRecorder
{
    public RecorderState State { get; private set; }

    public SessionRecord? Current { get; private set; }

    public TimeSpan Elapsed { get; set; }

    public AudioLevels Levels => default;

    public RecorderStats Stats => default;

    public bool IsScreenRecording => false;

    public event Action<RecorderState>? StateChanged;

    public event Action<RecorderWarning>? Warning
    {
        add { }
        remove { }
    }

    /// <summary>Starts a recording the way the recorder does: Starting, then Recording with the session set.</summary>
    public SessionRecord Start()
    {
        Elapsed = TimeSpan.Zero;
        Set(RecorderState.Starting);
        Current = new SessionRecord { Id = SessionId.New(), Title = "t", StartedAt = DateTimeOffset.UnixEpoch, State = SessionState.Recording };
        Set(RecorderState.Recording);
        return Current;
    }

    /// <summary>Puts the fake in a state without any other change (e.g. Stopping while the session is still current).</summary>
    public void Force(RecorderState state) => Set(state);

    public void Pause() => Set(RecorderState.Paused);

    public void Resume() => Set(RecorderState.Recording);

    /// <summary>Stops: Stopping, then Idle with no current session.</summary>
    public void Stop()
    {
        Set(RecorderState.Stopping);
        Current = null;
        Set(RecorderState.Idle);
    }

    private void Set(RecorderState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public ValueTask<SessionRecord> StartAsync(RecordingOptions options, CancellationToken cancellationToken) => ValueTask.FromResult(Start());

    public ValueTask SetScreenEnabledAsync(bool enabled, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask PauseAsync(CancellationToken cancellationToken)
    {
        Pause();
        return ValueTask.CompletedTask;
    }

    public ValueTask ResumeAsync(CancellationToken cancellationToken)
    {
        Resume();
        return ValueTask.CompletedTask;
    }

    public ValueTask<SessionRecord> StopAsync(CancellationToken cancellationToken)
    {
        var finished = Current!;
        Stop();
        return ValueTask.FromResult(finished);
    }
}
