using Murmur.Core.Capture;

namespace Murmur.App.Tests.Fakes;

/// <summary>An <see cref="ICapture"/> with configurable devices and capabilities. It never opens a source.</summary>
internal sealed class FakeCapture : ICapture
{
    public List<AudioDevice> Microphones { get; } =
    [
        new("mic-default", "Built-in microphone", AudioDeviceKind.Microphone, IsDefault: true),
        new("mic-usb", "USB headset", AudioDeviceKind.Microphone, IsDefault: false),
    ];

    public List<AudioDevice> Outputs { get; } =
    [
        new("out-default", "Speakers", AudioDeviceKind.SystemOutput, IsDefault: true),
        new("out-hdmi", "HDMI monitor", AudioDeviceKind.SystemOutput, IsDefault: false),
    ];

    public bool Screen { get; set; } = true;

    public CaptureCapabilities Capabilities => new(Microphones.Count > 0, Outputs.Count > 0, Screen);

    public IReadOnlyList<AudioDevice> GetAudioDevices(AudioDeviceKind kind) =>
        kind == AudioDeviceKind.Microphone ? [.. Microphones] : [.. Outputs];

    public IAudioSource CreateAudioSource(AudioDeviceKind kind, string? deviceId) => throw new NotSupportedException();

    public IReadOnlyList<DisplayInfo> GetDisplays() => [];

    public IScreenRecorder CreateScreenRecorder(ScreenCaptureOptions options) => throw new NotSupportedException();
}
