using Murmur.Core.Capture;

namespace Murmur.App.Tests.Fakes;

/// <summary>Scriptable <see cref="IAudioPlayer"/>: the test sets the position and state, every call is recorded.</summary>
internal sealed class FakeAudioPlayer : IAudioPlayer
{
    public PlaybackState State { get; set; } = PlaybackState.Closed;

    public TimeSpan Duration { get; set; }

    public TimeSpan Position { get; set; }

    public Exception? OpenFailure { get; set; }

    public List<IReadOnlyList<string>> Opens { get; } = [];

    public List<TimeSpan> Seeks { get; } = [];

    public int Plays { get; private set; }

    public int Pauses { get; private set; }

    public bool Disposed { get; private set; }

    public event Action<PlaybackState>? StateChanged;

    public ValueTask OpenAsync(IReadOnlyList<string> trackPaths, CancellationToken cancellationToken)
    {
        Opens.Add(trackPaths);
        if (OpenFailure is { } failure)
        {
            throw failure;
        }

        State = PlaybackState.Paused;
        return ValueTask.CompletedTask;
    }

    public void Play()
    {
        Plays++;
        Set(PlaybackState.Playing);
    }

    public void Pause()
    {
        Pauses++;
        Set(PlaybackState.Paused);
    }

    public void Seek(TimeSpan position)
    {
        Seeks.Add(position);
        Position = position;
    }

    /// <summary>Simulates the player reaching the end of the audio.</summary>
    public void End()
    {
        Position = Duration;
        Set(PlaybackState.Ended);
    }

    public void Dispose() => Disposed = true;

    private void Set(PlaybackState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}
