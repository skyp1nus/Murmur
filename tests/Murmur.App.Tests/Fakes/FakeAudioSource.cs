using Murmur.Core.Capture;

namespace Murmur.App.Tests.Fakes;

/// <summary>An <see cref="IAudioSource"/> that delivers whatever the test emits, on the caller's thread.</summary>
internal sealed class FakeAudioSource(AudioDevice device) : IAudioSource
{
    public AudioDevice Device { get; } = device;

    public AudioFormat Format { get; } = new(48_000, 1);

    public IAudioSink? Sink { get; private set; }

    public Exception? StartFailure { get; set; }

    public bool Started { get; private set; }

    public bool Stopped { get; private set; }

    public bool Disposed { get; private set; }

    public event Action<CaptureFault>? Faulted;

    public void Start(IAudioSink sink)
    {
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

    public void Dispose() => Disposed = true;

    /// <summary>Pushes samples to the sink the way the capture thread does.</summary>
    public void Emit(params float[] samples) => Sink?.Write(samples);

    public void Fault(string message) => Faulted?.Invoke(new CaptureFault(Device.Name, message, IsFatal: true));
}
