using System.Diagnostics;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Tests;

public sealed class WindowsCaptureTests(ITestOutputHelper output)
{
    private const string WindowsOnly = "WASAPI capture tests run only on Windows.";
    private static readonly TimeSpan CaptureDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CaptureTolerance = TimeSpan.FromMilliseconds(150);

    [Fact]
    public void GetDisplays_IsEmptyUntilScreenCaptureExists() =>
        Assert.Empty(new WindowsCapture().GetDisplays());

    [Fact]
    public void CreateScreenRecorder_IsNotSupportedYet() =>
        Assert.Throws<NotSupportedException>(() => new WindowsCapture().CreateScreenRecorder(new ScreenCaptureOptions()));

    [Fact]
    [Trait("Category", "Windows")]
    public void Capabilities_MatchEnumeration_AndScreenIsOff()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var capture = new WindowsCapture();

        var caps = capture.Capabilities;

        Assert.False(caps.Screen);
        Assert.Equal(caps.Microphone, capture.GetAudioDevices(AudioDeviceKind.Microphone).Count > 0);
        Assert.Equal(caps.SystemAudio, capture.GetAudioDevices(AudioDeviceKind.SystemOutput).Count > 0);
    }

    [Theory]
    [Trait("Category", "Windows")]
    [InlineData(AudioDeviceKind.Microphone)]
    [InlineData(AudioDeviceKind.SystemOutput)]
    public void GetAudioDevices_ReturnsNamedEndpoints_WithAtMostOneDefault(AudioDeviceKind kind)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        var devices = new WindowsCapture().GetAudioDevices(kind);

        foreach (var device in devices)
        {
            output.WriteLine($"{kind}: '{device.Name}' default={device.IsDefault} id={device.Id}");
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
            Assert.Equal(kind, device.Kind);
        }

        Assert.InRange(devices.Count(d => d.IsDefault), 0, 1);
    }

    [Theory]
    [Trait("Category", "Windows")]
    [InlineData(AudioDeviceKind.SystemOutput)]
    [InlineData(AudioDeviceKind.Microphone)]
    public void Capture_ThreeSeconds_DeliversWallClockFrames(AudioDeviceKind kind)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var capture = new WindowsCapture();
        Assert.SkipWhen(capture.GetAudioDevices(kind).Count == 0, $"No active {kind} endpoint on this machine.");

        using var source = capture.CreateAudioSource(kind, null);
        output.WriteLine($"{source.Device.Name}: {source.Format.SampleRate} Hz, {source.Format.Channels} ch");
        Assert.True(source.Format.SampleRate > 0);
        Assert.True(source.Format.Channels > 0);
        var faults = new List<CaptureFault>();
        source.Faulted += f => { lock (faults) { faults.Add(f); } };
        var sink = new CountingSink(source.Format.Channels);

        var stopwatch = Stopwatch.StartNew();
        source.Start(sink);
        Thread.Sleep(CaptureDuration);
        source.Stop();
        stopwatch.Stop();

        var seconds = sink.Frames / (double)source.Format.SampleRate;
        output.WriteLine($"frames={sink.Frames} ({seconds:F3} s, of which silence {sink.SilenceFrames / (double)source.Format.SampleRate:F3} s), writes={sink.Writes}, wall={stopwatch.Elapsed.TotalSeconds:F3} s");
        Assert.Empty(faults);
        Assert.Equal(0, sink.BadLengths);
        Assert.InRange(seconds, (CaptureDuration - CaptureTolerance).TotalSeconds, (CaptureDuration + CaptureTolerance).TotalSeconds);
        Assert.True(seconds <= stopwatch.Elapsed.TotalSeconds + 0.001, "the source ran ahead of wall clock");

        // No sink calls after Stop returns.
        var framesAtStop = sink.Frames;
        Thread.Sleep(200);
        Assert.Equal(framesAtStop, sink.Frames);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void StopAndDispose_AreIdempotent_AndSourceRestarts()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var capture = new WindowsCapture();
        Assert.SkipWhen(capture.GetAudioDevices(AudioDeviceKind.SystemOutput).Count == 0, "No active render endpoint on this machine.");

        var source = capture.CreateAudioSource(AudioDeviceKind.SystemOutput, null);
        source.Stop(); // never started
        var sink = new CountingSink(source.Format.Channels);
        source.Start(sink);
        Assert.Throws<InvalidOperationException>(() => source.Start(sink));
        Thread.Sleep(300);
        source.Stop();
        source.Stop();
        source.Start(new CountingSink(source.Format.Channels));
        source.Dispose();
        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.Start(sink));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void CreateAudioSource_ByDeviceId_UsesThatDevice()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var capture = new WindowsCapture();
        var devices = capture.GetAudioDevices(AudioDeviceKind.SystemOutput);
        Assert.SkipWhen(devices.Count == 0, "No active render endpoint on this machine.");

        using var source = capture.CreateAudioSource(AudioDeviceKind.SystemOutput, devices[^1].Id);

        Assert.Equal(devices[^1].Id, source.Device.Id);
        Assert.Equal(devices[^1].Name, source.Device.Name);
    }

    [Theory]
    [Trait("Category", "Windows")]
    [InlineData(AudioDeviceKind.SystemOutput)]
    [InlineData(AudioDeviceKind.Microphone)]
    public void SinkThrowingOnFirstWrite_RaisesFaultedOnce_AndStopFromHandlerDoesNotDeadlock(AudioDeviceKind kind)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var capture = new WindowsCapture();
        Assert.SkipWhen(capture.GetAudioDevices(kind).Count == 0, $"No active {kind} endpoint on this machine.");

        var source = capture.CreateAudioSource(kind, null);
        var faults = 0;
        var faulted = new ManualResetEventSlim(false);
        source.Faulted += _ =>
        {
            Interlocked.Increment(ref faults);
            source.Stop(); // re-entrant Stop on the capture thread must not block or self-join
            faulted.Set();
        };

        var owner = new Thread(() =>
        {
            source.Start(new ThrowingSink());
            Assert.True(faulted.Wait(TimeSpan.FromSeconds(5), CancellationToken.None), "Faulted was not raised.");
            source.Stop();  // owner joins and releases
            source.Stop();  // idempotent
            source.Dispose();
        })
        { IsBackground = true };
        owner.Start();

        Assert.True(owner.Join(TimeSpan.FromSeconds(15)), "Deadlock: Start/Stop did not return.");
        Assert.Equal(1, Volatile.Read(ref faults));
    }

    private sealed class ThrowingSink : IAudioSink
    {
        public void Write(ReadOnlySpan<float> interleaved) => throw new InvalidOperationException("sink failure");

        public void WriteSilence(int frames) => throw new InvalidOperationException("sink failure");
    }
}
