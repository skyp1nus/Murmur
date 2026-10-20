using Murmur.Core.Capture;

namespace Murmur.App.Tests.Fakes;

/// <summary>An <see cref="IAudioSource"/> that delivers whatever the test emits, on the caller's thread.</summary>
internal sealed class FakeAudioSource(AudioDevice device) : IAudioSource
{
    public AudioDevice Device { get; } = device;

    public AudioFormat Format { get; } = new(48_000, 1);

    public IAudioSink? Sink { get; private set; }

    public Exception? StartFailure { get; set; }

    /// <summary>Thrown by <see cref="Dispose"/> (a device that is already gone).</summary>
    public Exception? DisposeFailure { get; set; }

    /// <summary>Somebody listened to <see cref="Faulted"/> when <see cref="Start"/> ran.</summary>
    public bool FaultedHandledAtStart { get; private set; }

    public bool Started { get; private set; }

    public bool Stopped { get; private set; }

    public bool Disposed { get; private set; }

    public event Action<CaptureFault>? Faulted;

    public void Start(IAudioSink sink)
    {
        FaultedHandledAtStart = Faulted is not null;
        if (StartFailure is { } failure)
        {
            throw failure;
        }

        Sink = sink;
        Started = true;
    }

    public void Stop()
    {
        Stopped = true;
        Sink = null;
    }

    public void Dispose()
    {
        Disposed = true;
        if (DisposeFailure is { } failure)
        {
            throw failure;
        }
    }

    /// <summary>Pushes samples to the sink the way the capture thread does.</summary>
    public void Emit(params float[] samples) => Sink?.Write(samples);

    public void Fault(string message) => Faulted?.Invoke(new CaptureFault(Device.Name, message, IsFatal: true));
}
