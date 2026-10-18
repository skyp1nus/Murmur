using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Murmur.Capture.Windows.Audio;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Tests;

public sealed class WindowsCaptureTests(ITestOutputHelper output)
{
    private const string WindowsOnly = "WASAPI capture tests run only on Windows.";
    private static readonly TimeSpan CaptureDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CaptureTolerance = TimeSpan.FromMilliseconds(150);

    [Fact]
    [Trait("Category", "Windows")]
    public void Capabilities_MatchEnumeration()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var capture = new WindowsCapture();

        var caps = capture.Capabilities;

        Assert.Equal(Screen.CaptureInterop.IsSupported(), caps.Screen);
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

        using var source = CreateOrSkip(capture, kind);
        output.WriteLine($"{source.Device.Name}: {source.Format.SampleRate} Hz, {source.Format.Channels} ch");
        Assert.True(source.Format.SampleRate > 0);
        Assert.True(source.Format.Channels > 0);
        var faults = new List<CaptureFault>();
        source.Faulted += f => { lock (faults) { faults.Add(f); } };
        var sink = new CountingSink(source.Format.Channels);

        var stopwatch = Stopwatch.StartNew();
        StartOrSkip(source, sink);
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

        var source = CreateOrSkip(capture, AudioDeviceKind.SystemOutput);
        source.Stop(); // never started
        var sink = new CountingSink(source.Format.Channels);
        StartOrSkip(source, sink);
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

        using var source = CreateOrSkip(capture, AudioDeviceKind.SystemOutput, devices[^1].Id);

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

        var source = CreateOrSkip(capture, kind);
        var faults = 0;
        var faulted = new ManualResetEventSlim(false);
        source.Faulted += _ =>
        {
            Interlocked.Increment(ref faults);
            source.Stop(); // re-entrant Stop on the capture thread must not block or self-join
            faulted.Set();
        };

        // Nothing may escape the owner thread: an unhandled exception there would crash the test host.
        Exception? ownerError = null;
        var owner = new Thread(() =>
        {
            try
            {
                source.Start(new ThrowingSink());
                Assert.True(faulted.Wait(TimeSpan.FromSeconds(5), CancellationToken.None), "Faulted was not raised.");
                source.Stop();  // owner joins and releases
                source.Stop();  // idempotent
            }
            catch (Exception ex)
            {
                ownerError = ex;
            }
            finally
            {
                source.Dispose();
            }
        })
        { IsBackground = true };
        owner.Start();

        Assert.True(owner.Join(TimeSpan.FromSeconds(15)), "Deadlock: Start/Stop did not return.");
        if (ownerError is UnauthorizedAccessException denied)
        {
            Assert.Skip(PrivacySkip(denied));
        }

        if (ownerError is not null)
        {
            ExceptionDispatchInfo.Capture(ownerError).Throw();
        }

        Assert.Equal(1, Volatile.Read(ref faults));
    }

    [Fact]
    public void AccessDenied_IsTranslatedIntoAPrivacyHint()
    {
        // Pure logic, runs everywhere: E_ACCESSDENIED from IAudioClient.Initialize (as seen in CI) anywhere in the chain.
        var start = new InvalidOperationException(
            "start", new System.Runtime.InteropServices.COMException("IAudioClient.Initialize(SHARED, EVENTCALLBACK) failed", unchecked((int)0x80070005)));
        Assert.True(AudioAccess.IsAccessDenied(start));
        Assert.False(AudioAccess.IsAccessDenied(new InvalidOperationException("other", new System.Runtime.InteropServices.COMException("x", unchecked((int)0x88890004)))));
        Assert.False(AudioAccess.IsAccessDenied(null));

        var mic = AudioAccess.Denied(AudioDeviceKind.Microphone, "CABLE Output", start);
        Assert.StartsWith(AudioAccess.MicrophoneDeniedMessage, mic.Message, StringComparison.Ordinal);
        Assert.Contains("CABLE Output", mic.Message, StringComparison.Ordinal);
        Assert.Same(start, mic.InnerException);
        Assert.Equal(unchecked((int)0x80070005), mic.HResult);

        var loopback = AudioAccess.Denied(AudioDeviceKind.SystemOutput, "Speakers", start);
        Assert.StartsWith(AudioAccess.EndpointDeniedMessage, loopback.Message, StringComparison.Ordinal);
    }

    private static string PrivacySkip(UnauthorizedAccessException ex) =>
        "Windows privacy settings deny access to the endpoint (Settings → Privacy & security → Microphone): " + ex.Message;

    /// <summary>Opens a source; skips the test when Windows privacy settings deny access to the endpoint.</summary>
    private static IAudioSource CreateOrSkip(WindowsCapture capture, AudioDeviceKind kind, string? deviceId = null)
    {
        try
        {
            return capture.CreateAudioSource(kind, deviceId);
        }
        catch (UnauthorizedAccessException ex)
        {
            Assert.Skip(PrivacySkip(ex));
            throw;
        }
    }

    /// <summary>Starts a source; skips the test (after releasing it) when Windows denies access at IAudioClient.Initialize.</summary>
    private static void StartOrSkip(IAudioSource source, IAudioSink sink)
    {
        try
        {
            source.Start(sink);
        }
        catch (UnauthorizedAccessException ex)
        {
            source.Dispose();
            Assert.Skip(PrivacySkip(ex));
        }
    }

    private sealed class ThrowingSink : IAudioSink
    {
        public void Write(ReadOnlySpan<float> interleaved) => throw new InvalidOperationException("sink failure");

        public void WriteSilence(int frames) => throw new InvalidOperationException("sink failure");
    }
}
