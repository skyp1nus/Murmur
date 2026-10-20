using Murmur.Core.Capture;
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
/// <param name="ScreenFaults">Faults the screen recorder reported during this recording (fatal and non-fatal).</param>
public readonly record struct RecorderStats(long MicrophoneFrames, long SystemFrames, long DroppedFrames, long ScreenFramesDropped, int ScreenFaults = 0);

/// <summary>A problem during a recording that did not stop it.</summary>
/// <param name="Source">What reported it, e.g. <c>screen</c>; for logs.</param>
/// <param name="Message">Short Ukrainian text for the user; it is also kept in the session (see <see cref="SessionRecord.Error"/>).</param>
/// <param name="ScreenStopped">The screen recording ended because of it (the audio goes on).</param>
/// <param name="Kind">The kind of the underlying fault; the UI shows its own localized text for it.</param>
public sealed record RecorderWarning(string Source, string Message, bool ScreenStopped, CaptureFaultKind Kind = CaptureFaultKind.Other);

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

    /// <summary>A screen segment is being recorded now. Turns false when the screen is switched off or fails.</summary>
    bool IsScreenRecording { get; }

    /// <summary>Turns screen recording on or off while recording; each "on" starts a new screen segment.</summary>
    ValueTask SetScreenEnabledAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>Stops, flushes every file and returns the finished session in <see cref="SessionState.Recorded"/> state.</summary>
    ValueTask<SessionRecord> StopAsync(CancellationToken cancellationToken);

    event Action<RecorderState>? StateChanged;

    /// <summary>
    /// Raised off the UI thread when something went wrong without stopping the recording, e.g. the screen recorder
    /// faulted (the segment ends, the audio goes on) or fell back to a slower path. Each distinct problem is raised
    /// once per recording and added to the session's warnings.
    /// </summary>
    event Action<RecorderWarning>? Warning;
}
