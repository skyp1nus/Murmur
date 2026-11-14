using System.Globalization;
using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Recording;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.CrashHelper;

/// <summary>
/// Usage: <c>Murmur.Core.CrashHelper &lt;root-dir&gt; [speed] [pause-at-seconds]</c>. Starts a <see cref="Recorder"/> with
/// <see cref="SyntheticCapture"/> into a session under <c>root-dir</c>, prints <c>SESSION &lt;id&gt;</c>, then
/// <c>ELAPSED &lt;seconds&gt;</c> every 100 ms, forever. With <c>pause-at-seconds</c> the recorder is paused once that much
/// has been recorded (<c>PAUSED &lt;seconds&gt;</c> is printed) and the elapsed time stands still from then on. In that mode
/// the recording runs on a <see cref="SampleClock"/> that moves 100 ms per 100 ms of real time and waits for the sources, so
/// the pause lands on an exact sample whatever the load of the machine is. The crash test kills it mid-recording (or mid-pause).
/// With <c>--warn-at &lt;seconds&gt;</c> (anywhere in the arguments) a non-fatal screen fault is raised once that much has been
/// recorded (<c>WARNED</c> is printed). The helper's in-memory store dies with it, so as soon as the recorder has stored the
/// session's warnings the text is written to <see cref="StoredErrorFile"/> in the session folder (the stand-in for the database
/// row that survives a crash) and <c>STORED</c> is printed: the test kills the process after that and recovers from the file.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        double? warnAt = null;
        var warnIndex = Array.IndexOf(args, "--warn-at");
        if (warnIndex >= 0 && warnIndex + 1 < args.Length)
        {
            warnAt = double.Parse(args[warnIndex + 1], CultureInfo.InvariantCulture);
            args = [.. args.Where((_, i) => i != warnIndex && i != warnIndex + 1)];
        }

        if (args.Length < 1)
        {
            await Console.Error.WriteLineAsync("usage: Murmur.Core.CrashHelper <root-dir> [speed]");
            return 2;
        }

        var speed = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 1.0;
        double? pauseAt = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : null;
        var paused = false;
        var store = new InMemorySessionStore(args[0]);
        var clock = pauseAt is null ? null : new SampleClock();
        var synthetic = new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: speed, Screen: warnAt is not null, TimeProvider: clock));
        var screen = new RaisingScreenRecorder();
        ICapture capture = warnAt is null ? synthetic : new ScreenCapture(synthetic, screen);
        var recorder = new Recorder(capture, store, clock ?? TimeProvider.System, RecorderSettings.Default);

        var session = await recorder.StartAsync(new RecordingOptions(Title: "crash test", Screen: warnAt is not null), CancellationToken.None);
        Console.WriteLine($"SESSION {session.Id}");
        var warned = false;
        var stored = false;

        while (true)
        {
            await Task.Delay(100);
            clock?.Advance(TimeSpan.FromMilliseconds(100), synthetic);
            if (pauseAt is { } at && !paused && recorder.Elapsed.TotalSeconds >= at)
            {
                await recorder.PauseAsync(CancellationToken.None);
                paused = true;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"PAUSED {recorder.Elapsed.TotalSeconds:F3}"));
            }

            if (warnAt is { } warnTime && !warned && recorder.Elapsed.TotalSeconds >= warnTime)
            {
                screen.Raise(new CaptureFault("screen", "frames dropped", IsFatal: false, CaptureFaultKind.FramesDropped));
                warned = true;
                Console.WriteLine("WARNED");
            }

            if (warned && !stored && store.Updates.LastOrDefault(u => u.Error is not null) is { } withWarning)
            {
                File.WriteAllText(Path.Combine(store.GetFiles(session.Id).Directory, StoredErrorFile), withWarning.Error);
                stored = true;
                Console.WriteLine("STORED");
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ELAPSED {recorder.Elapsed.TotalSeconds:F3}"));
        }
    }

    /// <summary>The file (in the session folder) that holds the session's stored <c>Error</c> once the recorder has written it.</summary>
    internal const string StoredErrorFile = "stored-error.txt";

    private sealed class ScreenCapture(SyntheticCapture inner, IScreenRecorder screen) : ICapture
    {
        public CaptureCapabilities Capabilities => inner.Capabilities;

        public IReadOnlyList<AudioDevice> GetAudioDevices(AudioDeviceKind kind) => inner.GetAudioDevices(kind);

        public IAudioSource CreateAudioSource(AudioDeviceKind kind, string? deviceId) => inner.CreateAudioSource(kind, deviceId);

        public IReadOnlyList<DisplayInfo> GetDisplays() => inner.GetDisplays();

        public IScreenRecorder CreateScreenRecorder(ScreenCaptureOptions options) => screen;
    }

    /// <summary>A screen recorder that creates empty segment files and raises the fault it is told to, on the calling thread.</summary>
    private sealed class RaisingScreenRecorder : IScreenRecorder
    {
        public bool IsRecording { get; private set; }

        public ScreenRecorderStats Stats => new(0, 0, 0, "raising");

        public event Action<CaptureFault>? Faulted;

        public void Raise(CaptureFault fault) => Faulted?.Invoke(fault);

        public ValueTask StartSegmentAsync(string filePath, CancellationToken cancellationToken)
        {
            File.Create(filePath).Dispose();
            IsRecording = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopSegmentAsync(CancellationToken cancellationToken)
        {
            IsRecording = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
