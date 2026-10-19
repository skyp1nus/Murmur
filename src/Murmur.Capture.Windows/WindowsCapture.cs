using Murmur.Capture.Windows.Audio;
using Murmur.Capture.Windows.Playback;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Video;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows;

/// <summary>
/// Windows capture facade: WASAPI microphone and loopback, Windows.Graphics.Capture + Media Foundation screen recording,
/// WASAPI playback of a session's tracks (<see cref="IAudioPlayback"/>) and Media Engine playback of its screen segments
/// (<see cref="IVideoPlayback"/>): the composition root can register this one instance for all three interfaces.
/// </summary>
public sealed class WindowsCapture : ICapture, IAudioPlayback, IVideoPlayback
{
    private readonly WindowsVideoPlayback _video = new();

    /// <summary>
    /// Queried on every access (devices come and go): any active capture / render endpoint;
    /// <c>Screen</c> = <c>GraphicsCaptureSession.IsSupported()</c>.
    /// </summary>
    public CaptureCapabilities Capabilities =>
        new(
            Microphone: AudioEndpoints.HasActive(AudioDeviceKind.Microphone),
            SystemAudio: AudioEndpoints.HasActive(AudioDeviceKind.SystemOutput),
            Screen: CaptureInterop.IsSupported());

    /// <summary>Active endpoints of <paramref name="kind"/> with friendly names; <c>IsDefault</c> = default console endpoint.</summary>
    /// <exception cref="System.Runtime.InteropServices.COMException">The MMDevice API failed (e.g. the audio service is down).</exception>
    public IReadOnlyList<AudioDevice> GetAudioDevices(AudioDeviceKind kind) => AudioEndpoints.Enumerate(kind);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No default endpoint exists, or the endpoint is not active.</exception>
    /// <exception cref="NotSupportedException">The endpoint's mix format is not float32 or PCM16/24/32.</exception>
    public IAudioSource CreateAudioSource(AudioDeviceKind kind, string? deviceId) =>
        new WasapiAudioSource(AudioEndpoints.Open(kind, deviceId));

    /// <summary>Connected displays (EnumDisplayMonitors); <c>Id</c> is the GDI device name such as <c>\\.\DISPLAY1</c>.</summary>
    /// <exception cref="System.Runtime.InteropServices.COMException">EnumDisplayMonitors failed.</exception>
    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var entries = Displays.Enumerate();
        var displays = new DisplayInfo[entries.Count];
        for (var i = 0; i < displays.Length; i++)
        {
            displays[i] = entries[i].Info;
        }

        return displays;
    }

    /// <summary>
    /// A Windows.Graphics.Capture -> Media Foundation H.264 recorder for <see cref="ScreenCaptureOptions.DisplayId"/>
    /// (null = primary display). Nothing native is touched until the first segment starts.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">FrameRate outside 1..240, MaxHeight below 2 or BitrateKbps not positive.</exception>
    public IScreenRecorder CreateScreenRecorder(ScreenCaptureOptions options) => new WgcScreenRecorder(options);

    /// <summary>
    /// A closed <see cref="WasapiAudioPlayer"/> for the default render endpoint; nothing native is touched until
    /// <see cref="IAudioPlayer.OpenAsync"/>.
    /// </summary>
    public IAudioPlayer CreatePlayer() => new WasapiAudioPlayer();

    /// <summary>Media Foundation and the Media Engine are available (checked once, see <see cref="WindowsVideoPlayback"/>).</summary>
    public bool IsSupported => _video.IsSupported;

    /// <summary>A closed <see cref="MediaEngineVideoPlayer"/>; nothing native is touched until <see cref="IVideoPlayer.OpenAsync"/>.</summary>
    public IVideoPlayer CreateVideoPlayer() => _video.CreateVideoPlayer();
}
