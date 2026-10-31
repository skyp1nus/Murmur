using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Recording;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Recording;

/// <summary>
/// A recording that runs for a long time must not keep anything per frame, per poll or per pause: the managed heap that is
/// still alive after a full collection is the same at the start of the run and at its end. This is the "leak" half of the
/// recording memory budget (the other half, garbage that only waits for a collection, is <see cref="RecordingHeapTrimmer"/>).
/// </summary>
[Collection(RealtimeCollection.Name)]
public sealed class RecorderRetentionTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task LongRun_WithPollsPausesAndScreenToggles_KeepsNoPerFrameState()
    {
        var store = new InMemorySessionStore(_dir.Path);
        var capture = new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 40, Screen: true));
        var recorder = new Recorder(capture, store, TimeProvider.System, RecorderSettings.Default with { RingSeconds = 48 });
        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);

        async Task RunAsync(TimeSpan length, bool churn)
        {
            var end = DateTime.UtcNow + length;
            var round = 0;
            while (DateTime.UtcNow < end)
            {
                // What the Recording screen asks for at 4 Hz.
                _ = recorder.Levels;
                _ = recorder.Elapsed;
                _ = recorder.Stats;
                _ = recorder.IsScreenRecording;
                if (churn && ++round % 8 == 0)
                {
                    await recorder.PauseAsync(Ct);
                    await recorder.ResumeAsync(Ct);
                    await recorder.SetScreenEnabledAsync(round % 16 != 0, Ct);
                }

                await Task.Delay(25, Ct);
            }
        }

        await RunAsync(TimeSpan.FromSeconds(1.5), churn: true); // warm-up: JIT, pools, first segment
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        await RunAsync(TimeSpan.FromSeconds(5), churn: true);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var after = GC.GetTotalMemory(forceFullCollection: true);
        await recorder.StopAsync(Ct);

        TestContext.Current.TestOutputHelper?.WriteLine($"live {before} -> {after} bytes; allocated {allocated} bytes in 5 s");

        // Everything every thread of the process allocates while recording (capture threads, writer, flusher, the pause
        // handshake, the 4 Hz polls): measured ~70 KB in these 5 s, i.e. ~0.8 MB per minute.
        Assert.True(allocated < 512 * 1024, $"{allocated} bytes allocated in 5 s of recording at x40");
        Assert.True(after - before < 256 * 1024, $"live heap grew from {before} to {after} bytes during a 5 s run at x40");
    }
}
