using System.Globalization;

namespace Murmur.App.Diagnostics;

/// <summary>
/// Command line of the CI perf mode:
/// <c>--autorecord &lt;seconds&gt; [--screen] [--no-orb] [--no-mic] [--no-system] --perf-out &lt;json&gt; [--idle-seconds &lt;n&gt;]</c>.
/// </summary>
/// <param name="Seconds">How long to record.</param>
/// <param name="Screen">Also record the screen.</param>
/// <param name="PerfOut">Where the JSON report is written.</param>
/// <param name="IdleSeconds">How long the idle CPU is measured before recording (10 s in CI).</param>
/// <param name="ShowOrb">False (<c>--no-orb</c>) hides the orb, to see how much of the CPU it costs.</param>
/// <param name="Microphone">False (<c>--no-mic</c>) leaves the microphone out even when one exists.</param>
/// <param name="SystemAudio">False (<c>--no-system</c>) leaves the system loopback out.</param>
public sealed record AutoRecordOptions(
    int Seconds,
    bool Screen,
    string PerfOut,
    int IdleSeconds = AutoRecordOptions.DefaultIdleSeconds,
    bool ShowOrb = true,
    bool Microphone = true,
    bool SystemAudio = true)
{
    public const string Flag = "--autorecord";

    public const string ScreenFlag = "--screen";

    public const string NoOrbFlag = "--no-orb";

    public const string NoMicrophoneFlag = "--no-mic";

    public const string NoSystemAudioFlag = "--no-system";

    public const string PerfOutFlag = "--perf-out";

    public const string IdleSecondsFlag = "--idle-seconds";

    public const int DefaultIdleSeconds = 10;

    /// <summary>
    /// <c>true</c> when <c>--autorecord</c> is present and the whole command line is valid; <c>false</c> with an empty
    /// <paramref name="error"/> when the flag is absent (a normal start); <c>false</c> with a message when invalid.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, out AutoRecordOptions? options, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = null;
        error = string.Empty;

        var seconds = 0;
        var screen = false;
        string? perfOut = null;
        var idle = DefaultIdleSeconds;
        var present = false;
        var showOrb = true;
        var microphone = true;
        var systemAudio = true;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case Flag:
                    present = true;
                    if (!TryReadInt(args, ++i, out seconds) || seconds <= 0)
                    {
                        error = $"{Flag} needs a positive number of seconds.";
                        return false;
                    }

                    break;
                case ScreenFlag:
                    screen = true;
                    break;
                case NoOrbFlag:
                    showOrb = false;
                    break;
                case NoMicrophoneFlag:
                    microphone = false;
                    break;
                case NoSystemAudioFlag:
                    systemAudio = false;
                    break;
                case PerfOutFlag:
                    if (i + 1 >= args.Count || args[i + 1].Length == 0)
                    {
                        error = $"{PerfOutFlag} needs a file path.";
                        return false;
                    }

                    perfOut = args[++i];
                    break;
                case IdleSecondsFlag:
                    if (!TryReadInt(args, ++i, out idle) || idle < 0)
                    {
                        error = $"{IdleSecondsFlag} needs a non-negative number of seconds.";
                        return false;
                    }

                    break;
            }
        }

        if (!present)
        {
            return false;
        }

        if (perfOut is null)
        {
            error = $"{Flag} requires {PerfOutFlag} <file>.";
            return false;
        }

        if (!microphone && !systemAudio)
        {
            error = $"{NoMicrophoneFlag} and {NoSystemAudioFlag} together leave nothing to record.";
            return false;
        }

        options = new AutoRecordOptions(seconds, screen, perfOut, idle, showOrb, microphone, systemAudio);
        return true;
    }

    private static bool TryReadInt(IReadOnlyList<string> args, int index, out int value)
    {
        value = 0;
        return index < args.Count && int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
