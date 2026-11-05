namespace Murmur.Core.Calls;

/// <summary>
/// The monitor of a platform without audio-session enumeration (Linux development, CI, the design boards): it is not supported,
/// reports nothing and never raises <see cref="Changed"/>, so the Detect calls setting stays hidden and no call is ever detected.
/// </summary>
public sealed class NullAudioSessionMonitor : IAudioSessionMonitor
{
    private static readonly AudioActivitySnapshot Idle = new(false, []);

    public bool IsSupported => false;

    public AudioActivitySnapshot Snapshot() => Idle;

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}
