using Murmur.Capture.Windows.Audio;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows;

/// <summary>
/// Windows capture facade: WASAPI microphone and loopback. Screen capture is added by a separate task; until then
/// <see cref="CaptureCapabilities.Screen"/> is false, <see cref="GetDisplays"/> is empty and
/// <see cref="CreateScreenRecorder"/> throws <see cref="NotSupportedException"/>.
/// </summary>
public sealed class WindowsCapture : ICapture
{
    /// <summary>Queried on every access (devices come and go): any active capture / render endpoint.</summary>
    public CaptureCapabilities Capabilities =>
        new(
            Microphone: AudioEndpoints.HasActive(AudioDeviceKind.Microphone),
            SystemAudio: AudioEndpoints.HasActive(AudioDeviceKind.SystemOutput),
            Screen: false);

    /// <summary>Active endpoints of <paramref name="kind"/> with friendly names; <c>IsDefault</c> = default console endpoint.</summary>
    /// <exception cref="System.Runtime.InteropServices.COMException">The MMDevice API failed (e.g. the audio service is down).</exception>
    public IReadOnlyList<AudioDevice> GetAudioDevices(AudioDeviceKind kind) => AudioEndpoints.Enumerate(kind);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No default endpoint exists, or the endpoint is not active.</exception>
    /// <exception cref="NotSupportedException">The endpoint's mix format is not float32 or PCM16/24/32.</exception>
    public IAudioSource CreateAudioSource(AudioDeviceKind kind, string? deviceId) =>
        new WasapiAudioSource(AudioEndpoints.Open(kind, deviceId));

    public IReadOnlyList<DisplayInfo> GetDisplays() => [];

    public IScreenRecorder CreateScreenRecorder(ScreenCaptureOptions options) =>
        throw new NotSupportedException("Screen capture is not available in this build of Murmur.Capture.Windows yet.");
}
