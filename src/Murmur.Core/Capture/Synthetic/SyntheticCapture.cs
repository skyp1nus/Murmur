namespace Murmur.Core.Capture.Synthetic;

/// <param name="SpeedFactor">Audio seconds produced per wall-clock second (1 = real time; e.g. 120 for accelerated tests).</param>
/// <param name="Screen">Value of <see cref="CaptureCapabilities.Screen"/>.</param>
/// <param name="MicrophoneFaultAfter">Audio time after which the microphone fails (fatal fault), or <c>null</c>.</param>
/// <param name="SystemFaultAfter">Audio time after which the system source fails (fatal fault), or <c>null</c>.</param>
/// <param name="AudioLimit">
/// Test affordance: total audio each source emits before it goes quiet (without a fault), or <c>null</c> for no limit.
/// Lets a test record an exact amount of audio. Breaks the wall-clock continuity contract once reached.
/// </param>
/// <param name="TimeProvider">Clock that paces the sources; <c>null</c> = <see cref="TimeProvider.System"/>.</param>
public sealed record SyntheticCaptureOptions(
    double SpeedFactor = 1.0,
    bool Screen = false,
    TimeSpan? MicrophoneFaultAfter = null,
    TimeSpan? SystemFaultAfter = null,
    TimeSpan? AudioLimit = null,
    TimeProvider? TimeProvider = null);

/// <summary>
/// Platform-free <see cref="ICapture"/> for Linux development, the Linux desktop target and tests: a "Microphone" and a
/// "System" device that produce deterministic 48 kHz stereo signals (<see cref="SyntheticSignals"/>), and a screen
/// recorder that only creates empty segment files.
/// </summary>
public sealed class SyntheticCapture : ICapture
{
    public static readonly AudioDevice MicrophoneDevice = new("synthetic-mic", "Synthetic microphone", AudioDeviceKind.Microphone, IsDefault: true);

    public static readonly AudioDevice SystemDevice = new("synthetic-system", "Synthetic system output", AudioDeviceKind.SystemOutput, IsDefault: true);

    public static readonly DisplayInfo Display = new("synthetic-display", "Synthetic display", 1920, 1080, IsPrimary: true);

    private readonly List<SyntheticAudioSource> _sources = [];
    private readonly List<NullScreenRecorder> _screenRecorders = [];

    public SyntheticCapture(SyntheticCaptureOptions? options = null)
    {
        Options = options ?? new SyntheticCaptureOptions();
        Capabilities = new CaptureCapabilities(Microphone: true, SystemAudio: true, Screen: Options.Screen);
    }

    public SyntheticCaptureOptions Options { get; }

    public CaptureCapabilities Capabilities { get; }

    /// <summary>Every source created so far (for tests).</summary>
    public IReadOnlyList<SyntheticAudioSource> CreatedSources
    {
        get
        {
            lock (_sources)
            {
                return [.. _sources];
            }
        }
    }

    /// <summary>Every screen recorder created so far (for tests).</summary>
    public IReadOnlyList<NullScreenRecorder> CreatedScreenRecorders
    {
        get
        {
            lock (_screenRecorders)
            {
                return [.. _screenRecorders];
            }
        }
    }

    public IReadOnlyList<AudioDevice> GetAudioDevices(AudioDeviceKind kind) =>
        kind == AudioDeviceKind.Microphone ? [MicrophoneDevice] : [SystemDevice];

    public IAudioSource CreateAudioSource(AudioDeviceKind kind, string? deviceId)
    {
        var device = kind == AudioDeviceKind.Microphone ? MicrophoneDevice : SystemDevice;
        if (deviceId is not null && deviceId != device.Id)
        {
            throw new ArgumentException($"Unknown synthetic {kind} device '{deviceId}'.", nameof(deviceId));
        }

        var faultAfter = kind == AudioDeviceKind.Microphone ? Options.MicrophoneFaultAfter : Options.SystemFaultAfter;
        var source = new SyntheticAudioSource(device, Options.SpeedFactor, faultAfter, Options.TimeProvider, Options.AudioLimit);
        lock (_sources)
        {
            _sources.Add(source);
        }

        return source;
    }

    public IReadOnlyList<DisplayInfo> GetDisplays() => Options.Screen ? [Display] : [];

    public IScreenRecorder CreateScreenRecorder(ScreenCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var recorder = new NullScreenRecorder();
        lock (_screenRecorders)
        {
            _screenRecorders.Add(recorder);
        }

        return recorder;
    }
}
