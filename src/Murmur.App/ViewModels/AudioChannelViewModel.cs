using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.App.Services;
using Murmur.Core.Capture;

namespace Murmur.App.ViewModels;

public enum AudioChannelState
{
    /// <summary>Not started (the step is not shown).</summary>
    Idle,

    /// <summary>The source is being opened.</summary>
    Opening,

    /// <summary>The source delivers audio; the meter moves.</summary>
    Live,

    /// <summary>Windows refused access (privacy settings); only meaningful for the microphone.</summary>
    Denied,

    /// <summary>No device of this kind is plugged in.</summary>
    NoDevice,

    /// <summary>The source could not be opened or failed while running.</summary>
    Failed,
}

/// <summary>
/// One audio source of the audio step (the microphone or the system output) while the step is shown: opens it through
/// <see cref="ICapture.CreateAudioSource"/>, measures its peak level in an <see cref="AudioLevelMeter"/> and stops it again.
/// </summary>
/// <remarks>
/// <para>The audio callback only reads the samples and stores the loudest one in a field (<see cref="AudioLevelMeter.Write"/>);
/// the meter control polls <see cref="Meter"/> on its own timer. Opening and closing run off the UI thread: opening a
/// device can take a while and stopping joins the capture thread.</para>
/// <para>Starting again (another device was chosen, "Try again") supersedes a start that is still running: the generation
/// counter makes the older one close what it opened.</para>
/// </remarks>
public sealed partial class AudioChannelViewModel : ObservableObject
{
    private readonly ICapture _capture;
    private readonly AudioDeviceKind _kind;
    private readonly Func<string?> _deviceId;
    private readonly Action<Action> _post;
    private int _generation;
    private Running? _running;
    private Task _lastOperation = Task.CompletedTask;

    internal AudioChannelViewModel(ICapture capture, AudioDeviceKind kind, Func<string?> deviceId, Action<Action> post)
    {
        _capture = capture;
        _kind = kind;
        _deviceId = deviceId;
        _post = post;
    }

    /// <summary>The peak levels of the source, for the meter control (it is also the sink the capture thread writes to).</summary>
    public AudioLevelMeter Meter { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive), nameof(IsDenied), nameof(IsUnavailable))]
    public partial AudioChannelState State { get; set; }

    /// <summary>English reason of a failure (the exception message), or <c>null</c>.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool IsLive => State == AudioChannelState.Live;

    public bool IsDenied => State == AudioChannelState.Denied;

    /// <summary>The source cannot be used: denied, no device or failed.</summary>
    public bool IsUnavailable => State is AudioChannelState.Denied or AudioChannelState.NoDevice or AudioChannelState.Failed;

    /// <summary>The last start or stop (for tests).</summary>
    internal Task LastOperation => _lastOperation;

    /// <summary>Opens the source for the device chosen now; closes the one opened before.</summary>
    internal Task StartAsync()
    {
        var generation = ++_generation;
        var previous = _running;
        _running = null;
        State = AudioChannelState.Opening;
        Error = null;
        return _lastOperation = StartCoreAsync(generation, previous);
    }

    /// <summary>Closes the source.</summary>
    internal Task StopAsync()
    {
        ++_generation;
        var previous = _running;
        _running = null;
        if (State is AudioChannelState.Opening or AudioChannelState.Live)
        {
            State = AudioChannelState.Idle;
        }

        return _lastOperation = CloseAsync(previous);
    }

    private async Task StartCoreAsync(int generation, Running? previous)
    {
        await CloseAsync(previous).ConfigureAwait(true);
        if (generation != _generation)
        {
            return;
        }

        var id = _deviceId();
        var (running, error) = await Task.Run(() => Open(id)).ConfigureAwait(true);
        if (generation != _generation)
        {
            // Another start or a stop came while the device was opening: give this one back.
            await CloseAsync(running).ConfigureAwait(true);
            return;
        }

        if (running is not null)
        {
            _running = running;
            State = AudioChannelState.Live;
            return;
        }

        Error = error?.Message;
        State = error is null ? AudioChannelState.NoDevice : IsAccessDenied(error) ? AudioChannelState.Denied : AudioChannelState.Failed;
    }

    /// <summary>Opens and starts the source on a background thread. The fault handler is attached before the first buffer can arrive.</summary>
    private (Running? Running, Exception? Error) Open(string? deviceId)
    {
        IAudioSource? source = null;
        Action<CaptureFault>? handler = null;
        try
        {
            if (!HasDevice(deviceId))
            {
                return (null, null);
            }

            var created = _capture.CreateAudioSource(_kind, deviceId);
            source = created;
            handler = fault => OnFaulted(created, fault);
            created.Faulted += handler;
            created.Start(Meter);
            return (new Running(created, handler), null);
        }
        catch (Exception ex)
        {
            if (source is not null)
            {
                if (handler is not null)
                {
                    source.Faulted -= handler;
                }

                TryDispose(source);
            }

            return (null, ex);
        }
    }

    private bool HasDevice(string? deviceId)
    {
        var supported = _kind == AudioDeviceKind.Microphone ? _capture.Capabilities.Microphone : _capture.Capabilities.SystemAudio;
        if (!supported)
        {
            return false;
        }

        return deviceId is not null || _capture.GetAudioDevices(_kind).Count > 0;
    }

    private Task CloseAsync(Running? running)
    {
        if (running is null)
        {
            return Task.CompletedTask;
        }

        var source = running.Source;
        if (running.Handler is { } handler)
        {
            source.Faulted -= handler;
        }

        return Task.Run(() =>
        {
            try
            {
                source.Stop();
            }
            catch (Exception)
            {
                // A device that is already gone cannot be stopped; it is released below either way.
            }

            TryDispose(source);
        });
    }

    /// <summary>Releasing a device that is already gone may throw; it must not leave the channel stuck in "opening".</summary>
    private static void TryDispose(IAudioSource source)
    {
        try
        {
            source.Dispose();
        }
        catch (Exception)
        {
            // Nothing more can be done with it.
        }
    }

    private void OnFaulted(IAudioSource source, CaptureFault fault) => _post(() =>
    {
        if (_running is not { } running || !ReferenceEquals(running.Source, source))
        {
            return;
        }

        _running = null;
        ++_generation;
        Error = fault.Message;
        State = AudioChannelState.Failed;
        _lastOperation = CloseAsync(running);
    });

    /// <summary>Windows reports a refused microphone as an <see cref="UnauthorizedAccessException"/> (see D-023), possibly wrapped.</summary>
    private static bool IsAccessDenied(Exception error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            if (e is UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    private sealed record Running(IAudioSource Source, Action<CaptureFault>? Handler);
}
