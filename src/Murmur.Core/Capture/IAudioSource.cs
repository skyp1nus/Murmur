namespace Murmur.Core.Capture;

/// <summary>One platform audio stream (a microphone or a loopback of an output device).</summary>
public interface IAudioSource : IDisposable
{
    AudioDevice Device { get; }

    /// <summary>Native format of the stream; known once the source is created.</summary>
    AudioFormat Format { get; }

    /// <summary>Starts capturing on a dedicated thread. Every buffer is pushed to <paramref name="sink"/> on that thread.</summary>
    void Start(IAudioSink sink);

    /// <summary>Stops capturing and joins the capture thread. No <see cref="IAudioSink"/> calls happen after it returns.</summary>
    void Stop();

    /// <summary>Raised (rarely, possibly on the capture thread) when the device fails or disappears.</summary>
    event Action<CaptureFault>? Faulted;
}
