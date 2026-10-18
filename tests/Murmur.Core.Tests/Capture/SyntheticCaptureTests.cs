using System.Diagnostics;
using Murmur.Core.Capture;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Tests.Audio;
using Murmur.Core.Tests.Fakes;
using Murmur.Core.Tests.Recording;

namespace Murmur.Core.Tests.Capture;

[Collection(RealtimeCollection.Name)]
public sealed class SyntheticCaptureTests
{
    [Fact]
    public void Devices_AndCapabilities()
    {
        var capture = new SyntheticCapture();

        Assert.Equal(new CaptureCapabilities(true, true, false), capture.Capabilities);
        Assert.Equal([SyntheticCapture.MicrophoneDevice], capture.GetAudioDevices(AudioDeviceKind.Microphone));
        Assert.Equal([SyntheticCapture.SystemDevice], capture.GetAudioDevices(AudioDeviceKind.SystemOutput));
        Assert.Empty(capture.GetDisplays());
        Assert.True(new SyntheticCapture(new SyntheticCaptureOptions(Screen: true)).Capabilities.Screen);
    }

    [Fact]
    public void CreateAudioSource_ChecksTheDeviceId()
    {
        var capture = new SyntheticCapture();

        using var mic = capture.CreateAudioSource(AudioDeviceKind.Microphone, null);
        using var system = capture.CreateAudioSource(AudioDeviceKind.SystemOutput, SyntheticCapture.SystemDevice.Id);

        Assert.Equal(new AudioFormat(48_000, 2), mic.Format);
        Assert.Equal(AudioDeviceKind.SystemOutput, system.Device.Kind);
        Assert.Throws<ArgumentException>(() => capture.CreateAudioSource(AudioDeviceKind.Microphone, "nope"));
        Assert.Equal(2, capture.CreatedSources.Count);
    }

    [Fact]
    public void SystemSignal_SurvivesResamplingAndDecodesToTheBlockIndex()
    {
        const int blocks = 300;
        var input = new float[blocks * SyntheticSignals.BlockFrames * 2];
        SyntheticSignals.FillSystem(1_000 * SyntheticSignals.BlockFrames, input); // start at block 1000

        var output = StreamingResamplerTests.ResampleOneShot(input, SyntheticSignals.Format);

        Assert.Equal(blocks * SyntheticSignals.BlockSamples16k, output.Length);
        for (var b = 0; b < blocks; b++)
        {
            Assert.True(SyntheticSignals.TryDecodeSystemBlock(output.AsSpan(b * SyntheticSignals.BlockSamples16k), out var index), $"block {b}");
            Assert.Equal(1_000 + b, index);
        }
    }

    [Fact]
    public void SystemSignal_DecodesLargeIndices()
    {
        const long block = 0x01_23_45_67;
        var input = new float[3 * SyntheticSignals.BlockFrames * 2];
        SyntheticSignals.FillSystem((block - 1) * SyntheticSignals.BlockFrames, input);

        var output = StreamingResamplerTests.ResampleOneShot(input, SyntheticSignals.Format);

        Assert.True(SyntheticSignals.TryDecodeSystemBlock(output.AsSpan(SyntheticSignals.BlockSamples16k), out var index));
        Assert.Equal(block, index);
    }

    [Fact]
    public void SystemSignal_ShiftedBlocksDoNotDecodeAsTheExpectedIndex()
    {
        var input = new float[4 * SyntheticSignals.BlockFrames * 2];
        SyntheticSignals.FillSystem(0, input);
        var output = StreamingResamplerTests.ResampleOneShot(input, SyntheticSignals.Format);

        // Half a block off: the plateaus are read from the wrong positions.
        var ok = SyntheticSignals.TryDecodeSystemBlock(output.AsSpan(SyntheticSignals.BlockSamples16k + 80), out var index);
        Assert.False(ok && index == 1);
    }

    [Fact]
    public void MicrophoneSignal_Is440HzWithinItsEnvelope()
    {
        var peak = 0f;
        var crossings = 0;
        for (long i = 1; i < 48_000; i++)
        {
            var v = SyntheticSignals.Microphone(i);
            peak = Math.Max(peak, Math.Abs(v));
            if (SyntheticSignals.Microphone(i - 1) < 0 && v >= 0)
            {
                crossings++;
            }
        }

        Assert.InRange(crossings, 439, 441);
        Assert.InRange(peak, 0.45f, 0.5f);
    }

    [Fact]
    public async Task Source_EmitsTheCodedSignalAtTheRequestedPace()
    {
        using var source = new SyntheticAudioSource(SyntheticCapture.SystemDevice, speedFactor: 10);
        var sink = new CheckingSink();
        var watch = Stopwatch.StartNew();

        source.Start(sink);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        source.Stop();
        var elapsed = watch.Elapsed.TotalSeconds;

        Assert.Null(sink.Error);
        Assert.Equal(source.FramesEmitted, sink.Frames);
        var expected = elapsed * 48_000 * 10;
        Assert.InRange(sink.Frames, expected * 0.7, expected * 1.01);
        Assert.False(source.IsRunning);
    }

    [Fact]
    public async Task Source_FaultAfter_RaisesAFatalFaultAndGoesQuiet()
    {
        using var source = new SyntheticAudioSource(SyntheticCapture.MicrophoneDevice, speedFactor: 20, faultAfter: TimeSpan.FromSeconds(1));
        var faulted = new TaskCompletionSource<CaptureFault>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Faulted += f => faulted.TrySetResult(f);
        var sink = new CheckingSink(system: false);

        source.Start(sink);
        var fault = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        source.Stop();

        Assert.True(fault.IsFatal);
        Assert.Equal(48_000, sink.Frames);
        Assert.Null(sink.Error);
    }

    [Fact]
    public async Task Source_AudioLimit_StopsEmittingWithoutAFault()
    {
        using var source = new SyntheticAudioSource(SyntheticCapture.SystemDevice, speedFactor: 50, audioLimit: TimeSpan.FromSeconds(1));
        var faults = 0;
        source.Faulted += _ => faults++;
        var sink = new CheckingSink();

        source.Start(sink);
        while (source.FramesEmitted < 48_000)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await Task.Delay(100, TestContext.Current.CancellationToken);
        source.Stop();

        Assert.Equal(48_000, sink.Frames);
        Assert.Equal(0, faults);
    }

    [Fact]
    public async Task ScreenRecorder_CreatesEmptySegmentFiles()
    {
        using var dir = new TempDirectory();
        var capture = new SyntheticCapture(new SyntheticCaptureOptions(Screen: true));
        await using var screen = capture.CreateScreenRecorder(new ScreenCaptureOptions());
        var ct = TestContext.Current.CancellationToken;

        await screen.StartSegmentAsync(dir.File("screen-000.mp4"), ct);
        Assert.True(screen.IsRecording);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await screen.StartSegmentAsync(dir.File("x.mp4"), ct));
        await screen.StopSegmentAsync(ct);

        Assert.False(screen.IsRecording);
        Assert.True(File.Exists(dir.File("screen-000.mp4")));
        Assert.Equal(0, new FileInfo(dir.File("screen-000.mp4")).Length);
        Assert.Equal(0, screen.Stats.FramesCaptured);
    }

    /// <summary>Verifies every sample against the expected signal on the capture thread.</summary>
    private sealed class CheckingSink(bool system = true) : IAudioSink
    {
        public long Frames { get; private set; }

        public string? Error { get; private set; }

        public void Write(ReadOnlySpan<float> interleaved)
        {
            for (var i = 0; i < interleaved.Length / 2; i++)
            {
                var expected = system ? SyntheticSignals.System(Frames + i) : SyntheticSignals.Microphone(Frames + i);
                if (Error is null && (interleaved[2 * i] != expected || interleaved[(2 * i) + 1] != expected))
                {
                    Error = $"frame {Frames + i}";
                }
            }

            Frames += interleaved.Length / 2;
        }

        public void WriteSilence(int frames) => Error ??= "unexpected silence";
    }
}
