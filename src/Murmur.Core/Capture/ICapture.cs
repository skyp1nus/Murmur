namespace Murmur.Core.Capture;

public sealed record CaptureCapabilities(bool Microphone, bool SystemAudio, bool Screen);

/// <summary>Platform capture facade. The only implementation per OS lives in <c>Murmur.Capture.*</c>.</summary>
public interface ICapture
{
    CaptureCapabilities Capabilities { get; }

    IReadOnlyList<AudioDevice> GetAudioDevices(AudioDeviceKind kind);

    /// <summary>Opens an audio stream. <paramref name="deviceId"/> <c>null</c> means the system default device.</summary>
    IAudioSource CreateAudioSource(AudioDeviceKind kind, string? deviceId);

    IReadOnlyList<DisplayInfo> GetDisplays();

    IScreenRecorder CreateScreenRecorder(ScreenCaptureOptions options);
}
