using Murmur.App.Services;
using Murmur.Core.Capture;

namespace Murmur.App.Tests.Fakes;

/// <summary>A capture with displays for the settings tests (the shared <see cref="FakeCapture"/> lists none).</summary>
internal sealed class FakeDisplayCapture : ICapture
{
    private readonly FakeCapture _inner = new();

    public List<DisplayInfo> Displays { get; } =
    [
        new("display-1", "Display 1", 1920, 1080, IsPrimary: true),
        new("display-2", "Display 2", 2560, 1440, IsPrimary: false),
    ];

    public FakeCapture Inner => _inner;

    public CaptureCapabilities Capabilities => _inner.Capabilities;

    public IReadOnlyList<AudioDevice> GetAudioDevices(AudioDeviceKind kind) => _inner.GetAudioDevices(kind);

    public IAudioSource CreateAudioSource(AudioDeviceKind kind, string? deviceId) => _inner.CreateAudioSource(kind, deviceId);

    public IReadOnlyList<DisplayInfo> GetDisplays() => [.. Displays];

    public IScreenRecorder CreateScreenRecorder(ScreenCaptureOptions options) => throw new NotSupportedException();
}

internal sealed class FakeFolderLauncher : IFolderLauncher
{
    public List<string> Opened { get; } = [];

    public string? Failure { get; set; }

    public string? TryOpen(string path)
    {
        Opened.Add(path);
        return Failure;
    }
}

internal sealed class FakeFolderPicker : IFolderPicker
{
    public string? Result { get; set; }

    public List<(string Title, string? Start)> Calls { get; } = [];

    public Task<string?> PickFolderAsync(string title, string? startPath)
    {
        Calls.Add((title, startPath));
        return Task.FromResult(Result);
    }
}

internal sealed class FakePrivacyLauncher : IPrivacySettingsLauncher
{
    public bool CanOpen { get; set; } = true;

    public int Opened { get; private set; }

    public string? Failure { get; set; }

    public bool CanOpenMicrophoneSettings => CanOpen;

    public string? TryOpenMicrophoneSettings()
    {
        Opened++;
        return Failure;
    }
}
