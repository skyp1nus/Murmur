using System.Globalization;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Recording;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.CrashHelper;

/// <summary>
/// Usage: <c>Murmur.Core.CrashHelper &lt;root-dir&gt; [speed] [pause-at-seconds]</c>. Starts a <see cref="Recorder"/> with
/// <see cref="SyntheticCapture"/> into a session under <c>root-dir</c>, prints <c>SESSION &lt;id&gt;</c>, then
/// <c>ELAPSED &lt;seconds&gt;</c> every 100 ms, forever. With <c>pause-at-seconds</c> the recorder is paused once that much
/// has been recorded (<c>PAUSED &lt;seconds&gt;</c> is printed) and the elapsed time stands still from then on.
/// The crash test kills it mid-recording (or mid-pause).
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            await Console.Error.WriteLineAsync("usage: Murmur.Core.CrashHelper <root-dir> [speed]");
            return 2;
        }

        var speed = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 1.0;
        double? pauseAt = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : null;
        var paused = false;
        var store = new InMemorySessionStore(args[0]);
        var capture = new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: speed));
        var recorder = new Recorder(capture, store, TimeProvider.System, RecorderSettings.Default);

        var session = await recorder.StartAsync(new RecordingOptions(Title: "crash test"), CancellationToken.None);
        Console.WriteLine($"SESSION {session.Id}");

        while (true)
        {
            await Task.Delay(100);
            if (pauseAt is { } at && !paused && recorder.Elapsed.TotalSeconds >= at)
            {
                await recorder.PauseAsync(CancellationToken.None);
                paused = true;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"PAUSED {recorder.Elapsed.TotalSeconds:F3}"));
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ELAPSED {recorder.Elapsed.TotalSeconds:F3}"));
        }
    }
}
