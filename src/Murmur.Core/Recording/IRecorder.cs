using Murmur.Core.Sessions;

namespace Murmur.Core.Recording;

public enum RecorderState
{
    Idle,
    Starting,
    Recording,
    Stopping,
}

/// <param name="MicrophoneDeviceId"><c>null</c> = default microphone.</param>
/// <param name="SystemDeviceId"><c>null</c> = default output device.</param>
public sealed record RecordingOptions(
    bool Microphone = true,
    bool SystemAudio = true,
    bool Screen = false,
    string? MicrophoneDeviceId = null,
    string? SystemDeviceId = null,
    string? DisplayId = null,
    string? Title = null);

/// <summary>Peak levels in [0, 1] of the latest audio, for the recording orb.</summary>
public readonly record struct AudioLevels(float Microphone, float SystemAudio);

/// <param name="DroppedFrames">Frames lost because a buffer overflowed. Must stay 0.</param>
public readonly record struct RecorderStats(long MicrophoneFrames, long SystemFrames, long DroppedFrames, long ScreenFramesDropped);

/// <summary>Records one session at a time. Never waits on AI: heavy work starts only after <see cref="StopAsync"/>.</summary>
public interface IRecorder
{
    RecorderState State { get; }

    /// <summary>The session being recorded, or <c>null</c> when idle.</summary>
    SessionRecord? Current { get; }

    TimeSpan Elapsed { get; }

    /// <summary>Lock-free snapshot; safe to poll every frame.</summary>
    AudioLevels Levels { get; }

    RecorderStats Stats { get; }

    ValueTask<SessionRecord> StartAsync(RecordingOptions options, CancellationToken cancellationToken);

    /// <summary>Turns screen recording on or off while recording; each "on" starts a new screen segment.</summary>
    ValueTask SetScreenEnabledAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>Stops, flushes every file and returns the finished session in <see cref="SessionState.Recorded"/> state.</summary>
    ValueTask<SessionRecord> StopAsync(CancellationToken cancellationToken);

    event Action<RecorderState>? StateChanged;
}
