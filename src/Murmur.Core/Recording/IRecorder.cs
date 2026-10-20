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

/// <summary>Why the recorder tells the user about a problem; the App owns the words in both UI languages (see <see cref="SessionNotes"/>).</summary>
public enum RecorderWarningCode
{
    /// <summary>Windows privacy settings deny the microphone.</summary>
    MicrophoneDenied,

    /// <summary>Windows denies the system audio (loopback).</summary>
    SystemAudioDenied,

    /// <summary>The microphone could not start; <see cref="RecorderWarning.Detail"/> is the OS message.</summary>
    MicrophoneUnavailable,

    /// <summary>The system audio could not start; <see cref="RecorderWarning.Detail"/> is the OS message.</summary>
    SystemAudioUnavailable,

    /// <summary>Only the system audio is recorded because the microphone was lost.</summary>
    OnlySystemAudio,

    /// <summary>Only the microphone is recorded because the system audio was lost.</summary>
    OnlyMicrophone,

    /// <summary>The screen recorder skipped frames (the computer could not keep up).</summary>
    ScreenFramesDropped,

    /// <summary>The screen is recorded without GPU acceleration.</summary>
    ScreenCpuFallback,

    /// <summary>Another non-fatal screen problem.</summary>
    ScreenOther,

    /// <summary>The screen recording ended because of a fault; the audio goes on.</summary>
    ScreenStopped,
}

/// <summary>A problem during a recording that did not stop it.</summary>
/// <param name="Source">What reported it, e.g. <c>screen</c>; for logs.</param>
/// <param name="Code">What happened; the UI shows its own localized text for it (<see cref="SessionNotes.CodeOf"/> is the code stored in the session).</param>
/// <param name="ScreenStopped">The screen recording ended because of it (the audio goes on).</param>
/// <param name="Kind">The kind of the underlying fault.</param>
/// <param name="Detail">Optional parameter of the code (an OS message); never shown without the code's own text.</param>
public sealed record RecorderWarning(
    string Source,
    RecorderWarningCode Code,
    bool ScreenStopped,
    CaptureFaultKind Kind = CaptureFaultKind.Other,
    string? Detail = null);

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
