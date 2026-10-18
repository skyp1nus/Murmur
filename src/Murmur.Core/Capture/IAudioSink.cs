namespace Murmur.Core.Capture;

/// <summary>
/// Receives audio on the capture thread. This is a hot path: implementations must not allocate,
/// block, take locks, log or await. Copy into a lock-free buffer and return.
/// </summary>
public interface IAudioSink
{
    /// <summary>Interleaved float samples in the source's <see cref="AudioFormat"/>; length is a multiple of the channel count.</summary>
    void Write(ReadOnlySpan<float> interleaved);

    /// <summary>
    /// A gap of <paramref name="frames"/> frames that must be filled with silence to keep the track aligned
    /// with wall-clock time (silent packets, idle loopback, device glitch).
    /// </summary>
    void WriteSilence(int frames);
}
