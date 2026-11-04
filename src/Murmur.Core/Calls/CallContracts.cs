namespace Murmur.Core.Calls;

/// <summary>A process that has an active audio render (output) session.</summary>
public sealed record RenderingProcess(int ProcessId, string ProcessName);

/// <summary>What the platform audio stack reports right now.</summary>
public sealed record AudioActivitySnapshot(bool MicrophoneInUse, IReadOnlyList<RenderingProcess> Rendering);

/// <summary>Platform source of audio-session activity (Windows: WASAPI session enumeration). Sends nothing to the network.</summary>
public interface IAudioSessionMonitor : IDisposable
{
    /// <summary>False on platforms without an implementation (the Detect calls setting is then hidden).</summary>
    bool IsSupported { get; }

    AudioActivitySnapshot Snapshot();

    /// <summary>Raised off the UI thread when a session starts, stops or changes state (may be coalesced by the implementation).</summary>
    event Action? Changed;

    void Start();

    void Stop();
}

/// <param name="AppName">Friendly name (Teams, Zoom, Chrome, Edge, …; fallback: the process name).</param>
public sealed record CallInfo(string AppName, string ProcessName, int ProcessId);

public interface ICallDetector : IDisposable
{
    bool IsRunning { get; }

    /// <summary>Starts watching (subscribes to the monitor; no polling while stopped).</summary>
    void Start();

    void Stop();

    /// <summary>A call session started (raised once per call session, off the UI thread).</summary>
    event Action<CallInfo>? CallStarted;

    /// <summary>The call session ended (both mic and the app's render session inactive for 30 s).</summary>
    event Action<CallInfo>? CallEnded;
}
