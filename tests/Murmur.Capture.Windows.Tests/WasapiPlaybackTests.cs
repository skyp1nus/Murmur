using System.Diagnostics;
using Murmur.Capture.Windows.Audio;
using Murmur.Capture.Windows.Playback;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Tests;

/// <summary>Real WASAPI playback on the default render endpoint. Windows only; skipped when there is no output.</summary>
public sealed class WasapiPlaybackTests(ITestOutputHelper output)
{
    private const string WindowsOnly = "WASAPI playback tests run only on Windows.";
    private const int Rate = 16_000;
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(150);

    [Fact]
    [Trait("Category", "Windows")]
    public async Task TwoTracks_Play_Pause_Seek_End()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        Assert.SkipUnless(AudioEndpoints.HasActive(AudioDeviceKind.SystemOutput), "No active render endpoint on this machine.");

        using var dir = new TempDir();
        var mic = dir.Write("mic.wav", TestWav.Pcm16(Sine(3 * Rate, 440, 0.05)));
        var system = dir.Write("system.wav", TestWav.Pcm16(Sine(2 * Rate, 660, 0.05)));
        using var player = new WindowsCapture().CreatePlayer();
        var changes = new List<PlaybackState>();
        using var ended = new ManualResetEventSlim(false);
        player.StateChanged += s =>
        {
            lock (changes)
            {
                changes.Add(s);
            }

            if (s == PlaybackState.Ended)
            {
                ended.Set();
            }
        };
        ((WasapiAudioPlayer)player).Faulted += f => output.WriteLine($"fault: {f.Message}");

        await player.OpenAsync([mic, system], TestContext.Current.CancellationToken);
        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.Equal(TimeSpan.FromSeconds(3), player.Duration);

        // Play 1 s: the position follows the wall clock.
        var clock = Stopwatch.StartNew();
        player.Play();
        Thread.Sleep(1_000);
        var position = player.Position;
        var elapsed = clock.Elapsed;
        output.WriteLine($"after {elapsed.TotalMilliseconds:F0} ms: position {position.TotalMilliseconds:F0} ms");
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.InRange(position, elapsed - Tolerance, elapsed + Tolerance);

        // Pause holds the position.
        player.Pause();
        var paused = player.Position;
        Thread.Sleep(300);
        Assert.Equal(paused, player.Position);
        Assert.InRange(paused, position - Tolerance, position + Tolerance);

        // Seek while paused, then play from there.
        player.Seek(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), player.Position);
        clock.Restart();
        player.Play();
        Thread.Sleep(400);
        position = player.Position;
        elapsed = clock.Elapsed;
        output.WriteLine($"after seek + {elapsed.TotalMilliseconds:F0} ms: position {position.TotalMilliseconds:F0} ms");
        Assert.InRange(position, TimeSpan.FromSeconds(2) + elapsed - Tolerance, TimeSpan.FromSeconds(2) + elapsed + Tolerance);

        // The remaining ~0.6 s plays out and Ended fires once.
        Assert.True(ended.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken), "Ended did not fire.");
        Thread.Sleep(100);
        Assert.Equal(PlaybackState.Ended, player.State);
        Assert.Equal(player.Duration, player.Position);
        lock (changes)
        {
            Assert.Single(changes, s => s == PlaybackState.Ended);
        }

        // Play after Ended restarts from 0.
        player.Play();
        Thread.Sleep(200);
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.InRange(player.Position, TimeSpan.Zero, TimeSpan.FromMilliseconds(200) + Tolerance);
        Assert.Null(((WasapiAudioPlayer)player).LastFault);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public async Task SeekWhilePlaying_JumpsAndKeepsPlaying()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        Assert.SkipUnless(AudioEndpoints.HasActive(AudioDeviceKind.SystemOutput), "No active render endpoint on this machine.");

        using var dir = new TempDir();
        var mic = dir.Write("mic.wav", TestWav.Pcm16(Sine(5 * Rate, 300, 0.05)));
        using var player = new WasapiAudioPlayer();
        await player.OpenAsync([mic], TestContext.Current.CancellationToken);

        player.Play();
        Thread.Sleep(300);
        player.Seek(TimeSpan.FromSeconds(4));
        var clock = Stopwatch.StartNew();
        Thread.Sleep(300);
        var position = player.Position;
        var expected = TimeSpan.FromSeconds(4) + clock.Elapsed;

        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.InRange(position, expected - Tolerance, expected + Tolerance);

        player.Seek(TimeSpan.FromMinutes(5)); // clamps to the end while playing -> Ended
        Assert.Equal(PlaybackState.Ended, player.State);
        Assert.Equal(TimeSpan.FromSeconds(5), player.Position);
    }

    private static short[] Sine(int frames, double hz, double amplitude)
    {
        var samples = new short[frames];
        for (var i = 0; i < frames; i++)
        {
            samples[i] = (short)(Math.Sin(2 * Math.PI * hz * i / Rate) * amplitude * short.MaxValue);
        }

        return samples;
    }
}
