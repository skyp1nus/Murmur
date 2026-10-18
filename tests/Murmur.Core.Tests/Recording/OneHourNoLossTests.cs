using System.Diagnostics;
using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Recording;

/// <summary>
/// Phase 1 acceptance: "1 hour without losses". The full pipeline (synthetic capture threads -> rings -> writer thread
/// -> resampler -> WAV) runs over one hour of audio, accelerated by the synthetic sources' speed factor.
/// </summary>
/// <remarks>
/// At x<see cref="Speed"/> the default 4 s ring would be only 4 / 80 = 50 ms of wall time, so the ring is enlarged to
/// 48 s of audio (rounded up to 2^23 samples = 87 s, i.e. ~1.1 s of wall time) to keep a safety margin over the 20 ms
/// writer period on a loaded, shared CI machine. Everything else (writer period, flush interval, threads, resampler,
/// WAV writer) uses the production settings.
/// </remarks>
[Collection(RealtimeCollection.Name)]
public sealed class OneHourNoLossTests : IDisposable
{
    private const double Speed = 80;
    private const long HourFrames48k = 3_600L * 48_000;
    private const long HourSamples16k = 3_600L * 16_000;

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task OneHourOfAudio_IsRecordedWithoutLosses()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemorySessionStore(_dir.Path);
        var capture = new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: Speed, AudioLimit: TimeSpan.FromHours(1)));
        var settings = RecorderSettings.Default with { RingSeconds = 48 };
        var recorder = new Recorder(capture, store, TimeProvider.System, settings);
        var watch = Stopwatch.StartNew();

        var started = await recorder.StartAsync(new RecordingOptions(Title: "One hour"), ct);
        var sources = capture.CreatedSources;
        var deadline = TimeSpan.FromSeconds((3_600 / Speed) * 4);
        while ((sources[0].FramesEmitted < HourFrames48k || sources[1].FramesEmitted < HourFrames48k) && recorder.Stats.DroppedFrames == 0)
        {
            Assert.True(watch.Elapsed < deadline, "the synthetic sources did not produce an hour of audio in time");
            await Task.Delay(250, ct);
        }

        var finished = await recorder.StopAsync(ct);
        var report = $"1 h of audio recorded in {watch.Elapsed.TotalSeconds:F1} s (x{Speed}); writer: {recorder.WriterIterations} iterations, " +
            $"max cycle {recorder.WriterMaxCycle.TotalMilliseconds:F1} ms, max flush {recorder.WriterMaxFlush.TotalMilliseconds:F1} ms";
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        Assert.True(recorder.Stats.DroppedFrames == 0, $"{recorder.Stats.DroppedFrames} frames dropped; {report}");

        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.Equal(TimeSpan.FromHours(1), finished.Duration);
        Assert.Equal(new RecorderStats(HourFrames48k, HourFrames48k, 0, 0), recorder.Stats);
        Assert.Equal(0, recorder.WriterSteadyStateAllocatedBytes);

        var files = store.GetFiles(started.Id);
        VerifyTrack(files.MicrophoneAudio, SyntheticSignals.FillMicrophone, decodeBlocks: false);
        VerifyTrack(files.SystemAudio, SyntheticSignals.FillSystem, decodeBlocks: true);
    }

    /// <summary>
    /// The file must hold exactly 3600 s x 16000 samples and match, sample for sample, the reference conversion of the
    /// generated signal (any gap, repeat or corruption breaks the match). For the coded system signal every 10 ms block
    /// must also decode to its own index.
    /// </summary>
    private static void VerifyTrack(string path, Action<long, Span<float>> fill, bool decodeBlocks)
    {
        using var reader = WavReader.Open(path);
        Assert.Equal(AudioFormat.Asr, reader.Format);
        Assert.Equal(HourSamples16k, reader.FrameCount);
        Assert.Equal(HourSamples16k * 2, reader.Layout.DeclaredDataBytes);

        const int chunkFrames = 48_000;
        var resampler = new StreamingResampler(SyntheticSignals.Format);
        var input = new float[chunkFrames * 2];
        var expected = new float[resampler.MaxOutputSamples(chunkFrames) + resampler.MaxFlushSamples];
        var pcm = new byte[expected.Length * 2];
        var actual = new float[expected.Length];
        var block = new float[SyntheticSignals.BlockSamples16k];
        var blockFill = 0;
        long nextBlock = 0;
        long position = 0;

        for (long frame = 0; frame <= HourFrames48k; frame += chunkFrames)
        {
            int count;
            if (frame < HourFrames48k)
            {
                fill(frame, input);
                count = resampler.Process(input, expected);
            }
            else
            {
                count = resampler.Flush(expected);
            }

            // Reference samples as they come back from a PCM16 file.
            WavPcm16Writer.Convert(expected.AsSpan(0, count), pcm);
            for (var i = 0; i < count; i++)
            {
                expected[i] = BitConverter.ToInt16(pcm, 2 * i) / 32768f;
            }

            Assert.Equal(count, reader.Read(actual.AsSpan(0, count)));
            if (!expected.AsSpan(0, count).SequenceEqual(actual.AsSpan(0, count)))
            {
                var at = 0;
                while (expected[at] == actual[at])
                {
                    at++;
                }

                Assert.Fail($"{Path.GetFileName(path)} differs from the reference at sample {position + at} ({TimeSpan.FromSeconds((position + at) / 16_000.0)})");
            }

            if (decodeBlocks)
            {
                for (var i = 0; i < count; i++)
                {
                    block[blockFill++] = actual[i];
                    if (blockFill == block.Length)
                    {
                        Assert.True(SyntheticSignals.TryDecodeSystemBlock(block, out var index), $"block {nextBlock} does not decode");
                        Assert.Equal(nextBlock, index);
                        nextBlock++;
                        blockFill = 0;
                    }
                }
            }

            position += count;
        }

        Assert.Equal(HourSamples16k, position);
        Assert.Equal(0, reader.Read(actual));
        if (decodeBlocks)
        {
            Assert.Equal(360_000, nextBlock);
        }
    }
}
