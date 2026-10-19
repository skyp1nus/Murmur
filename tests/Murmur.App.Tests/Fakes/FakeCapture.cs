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

    /// <summary>Every source opened so far, in order.</summary>
    public List<FakeAudioSource> Sources { get; } = [];

    /// <summary>Failure of <see cref="CreateAudioSource"/> per kind (for example access denied); <c>null</c> = works.</summary>
    public Dictionary<AudioDeviceKind, Exception> CreateFailures { get; } = [];

    /// <summary>Failure of <see cref="IAudioSource.Start"/> per kind (Windows reports a refused microphone there).</summary>
    public Dictionary<AudioDeviceKind, Exception> StartFailures { get; } = [];

    public List<(AudioDeviceKind Kind, string? DeviceId)> Opened { get; } = [];

    public IAudioSource CreateAudioSource(AudioDeviceKind kind, string? deviceId)
    {
        lock (Sources)
        {
            Opened.Add((kind, deviceId));
        }

        if (CreateFailures.TryGetValue(kind, out var failure))
        {
            throw failure;
        }

        var devices = kind == AudioDeviceKind.Microphone ? Microphones : Outputs;
        var device = devices.Find(d => d.Id == deviceId) ?? devices.Find(d => d.IsDefault) ?? new AudioDevice("none", "none", kind, IsDefault: true);
        var source = new FakeAudioSource(device) { StartFailure = StartFailures.GetValueOrDefault(kind) };
        lock (Sources)
        {
            Sources.Add(source);
        }

        return source;
    }

    /// <summary>The newest source of a kind.</summary>
    public FakeAudioSource Latest(AudioDeviceKind kind)
    {
        lock (Sources)
        {
            return Sources.Last(s => s.Device.Kind == kind);
        }
    }

    public IReadOnlyList<DisplayInfo> GetDisplays() => [];

    public IScreenRecorder CreateScreenRecorder(ScreenCaptureOptions options) => throw new NotSupportedException();
}
