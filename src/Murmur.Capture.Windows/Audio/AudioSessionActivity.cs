using Murmur.Core.Calls;

namespace Murmur.Capture.Windows.Audio;

/// <summary>One audio session as WASAPI reports it: which endpoint flow, whose process, what state.</summary>
/// <param name="IsCapture">The session is on a capture (microphone) endpoint; otherwise on a render (output) endpoint.</param>
/// <param name="ProcessId">The process of the session; 0 for a cross-process session.</param>
/// <param name="State">AudioSessionState: 0 inactive, 1 active, 2 expired.</param>
/// <param name="IsSystemSounds">The system-sounds session (notification dings).</param>
internal readonly record struct AudioSessionReading(bool IsCapture, int ProcessId, int State, bool IsSystemSounds);

/// <summary>
/// The platform-independent part of <see cref="WasapiAudioSessionMonitor"/>: turns the session readings of the default endpoints into
/// the <see cref="AudioActivitySnapshot"/> the call detector reads. Runnable (and tested) without Windows.
/// </summary>
internal static class AudioSessionActivity
{
    /// <summary>AudioSessionTypes.h: <c>AudioSessionStateActive</c>.</summary>
    public const int StateActive = 1;

    /// <summary>
    /// The microphone is in use when another process has an active capture session; every process with an active render session
    /// (not the system sounds, not a cross-process session, not this process) is rendering, once, in the order first seen.
    /// </summary>
    /// <param name="readings">The sessions of the default render and capture endpoints.</param>
    /// <param name="ownProcessId">Murmur's own process: its recording never makes the mic "in use" and its playback never renders.</param>
    /// <param name="nameOf">The process name of a process id (the monitor caches it).</param>
    public static AudioActivitySnapshot Build(IEnumerable<AudioSessionReading> readings, int ownProcessId, Func<int, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(nameOf);
        var microphone = false;
        var rendering = new List<RenderingProcess>();
        foreach (var reading in readings)
        {
            if (reading.State != StateActive || reading.IsSystemSounds || reading.ProcessId <= 0 || reading.ProcessId == ownProcessId)
            {
                continue;
            }

            if (reading.IsCapture)
            {
                microphone = true;
            }
            else if (!rendering.Exists(r => r.ProcessId == reading.ProcessId))
            {
                rendering.Add(new RenderingProcess(reading.ProcessId, nameOf(reading.ProcessId)));
            }
        }

        return new AudioActivitySnapshot(microphone, rendering);
    }

    /// <summary>Whether two snapshots report the same thing (mic state and the set of rendering process ids): no change to report.</summary>
    public static bool Same(AudioActivitySnapshot left, AudioActivitySnapshot right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.MicrophoneInUse != right.MicrophoneInUse || left.Rendering.Count != right.Rendering.Count)
        {
            return false;
        }

        foreach (var process in left.Rendering)
        {
            var found = false;
            foreach (var other in right.Rendering)
            {
                if (other.ProcessId == process.ProcessId)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The distinct endpoint ids (the console and communications defaults are often one device), nulls dropped.</summary>
    public static IReadOnlyList<string> DistinctEndpoints(params string?[] ids)
    {
        var result = new List<string>(ids.Length);
        foreach (var id in ids)
        {
            if (!string.IsNullOrEmpty(id) && !result.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(id);
            }
        }

        return result;
    }
}
