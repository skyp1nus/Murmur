using Murmur.Capture.Windows.Playback;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Tests;

/// <summary>Playback speed in the WASAPI player: the player contract (scripted engine) and the stretcher on a real <see cref="TrackMixer"/>.</summary>
public sealed class WasapiPlayerSpeedTests : IDisposable
{
    private const int Rate = 16_000;

    private readonly TempDir _dir = new();
    private readonly FakeEngineFactory _engines = new();
    private readonly WasapiAudioPlayer _player;
    private readonly string _mic;

    public WasapiPlayerSpeedTests()
    {
        _mic = _dir.Write("mic.wav", TestWav.Pcm16(TestWav.Constant(3 * Rate, 100)));
        _player = new WasapiAudioPlayer(_engines.Create);
    }

    public void Dispose()
    {
        _player.Dispose();
        _dir.Dispose();
    }

    private void Open() => _player.OpenAsync([_mic], TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult();

    [Fact]
    public void ThePlayer_CanChangeSpeed_StartsAt1()
    {
        Assert.True(_player.CanChangeSpeed);
        Assert.Equal(1, _player.Speed);
    }

    [Theory]
    [InlineData(1.25, 1.25)]
    [InlineData(0.75, 0.75)]
    [InlineData(2, 2)]
    [InlineData(5, 2)]
    [InlineData(0.1, 0.5)]
    [InlineData(double.NaN, 1)]
    public void SetSpeed_ClampsAndReportsTheSpeedInEffect(double asked, double expected)
    {
        _player.SetSpeed(asked);

        Assert.Equal(expected, _player.Speed);
    }

    [Fact]
    public void SetSpeed_ReachesTheEngine_AlsoWhilePlaying_AndOfEnginesCreatedLater()
    {
        _player.SetSpeed(1.5); // before opening
        Open();
        Assert.Equal(1.5, _engines.Last.Speed);

        _player.Play();
        _player.SetSpeed(2);
        Assert.Equal(2, _engines.Last.Speed);
        Assert.Equal(PlaybackState.Playing, _player.State);

        Open(); // the tracks are opened again: the new engine plays at the speed in effect
        Assert.Equal(2, _engines.Last.Speed);
        Assert.Equal(2, _player.Speed);
    }

    [Fact]
    public void ARestartedEngine_AfterAFault_KeepsTheSpeed()
    {
        Open();
        _player.SetSpeed(0.75);
        _player.Play();
        _engines.Last.FireFault();
        Assert.Equal(PlaybackState.Paused, _player.State);

        _player.Play();

        Assert.Equal(2, _engines.Engines.Count);
        Assert.Equal(0.75, _engines.Last.Speed);
    }

    [Fact]
    public void Position_IsTheEnginesMediaFrame_NotScaledAgain()
    {
        Open();
        _player.SetSpeed(2);
        _player.Play();

        _engines.Last.PlayedFrame = 2 * Rate; // two seconds of recording heard (one second of real time at 2x)

        Assert.Equal(TimeSpan.FromSeconds(2), _player.Position);
        Assert.Equal(TimeSpan.FromSeconds(3), _player.Duration);
    }

    [Fact]
    public void SetSpeed_AfterDispose_DoesNotThrow()
    {
        Open();
        _player.Dispose();

        _player.SetSpeed(1.5);

        Assert.Equal(1.5, _player.Speed);
    }

    [Fact]
    public void PlayedMediaFrame_IsTheSubmittedMediaMinusThePaddingAtTheOutputSpeed()
    {
        Assert.Equal(PlaybackTiming.PlayedFrame(1_000, 4_000, 800, 100_000), PlaybackTiming.PlayedMediaFrame(1_000, 5_000, 800, 1, 100_000));
        Assert.Equal(5_000 - (800 * 2), PlaybackTiming.PlayedMediaFrame(1_000, 5_000, 800, 2, 100_000));
        Assert.Equal(5_000 - 600, PlaybackTiming.PlayedMediaFrame(1_000, 5_000, 800, 0.75, 100_000));
        Assert.Equal(1_000, PlaybackTiming.PlayedMediaFrame(1_000, 1_000, 800, 2, 100_000)); // never before where it started
        Assert.Equal(100_000, PlaybackTiming.PlayedMediaFrame(1_000, 120_000, 0, 1.5, 100_000)); // never past the end
    }

    [Fact]
    public void TheStretcher_OnTheMixer_ReadsEachFrameOnce_AndMuteStillFades()
    {
        // Track 0 = 1000, track 1 = 5000 (constant): the mix is 6000, and 1000 once track 1 is muted - the fade is the mixer's.
        using var mixer = new TrackMixer([TestWav.Reader(TestWav.Constant(6 * Rate, 1_000)), TestWav.Reader(TestWav.Constant(6 * Rate, 5_000))]);
        var stretcher = new WsolaStretcher(mixer, Rate);
        stretcher.Seek(0);
        stretcher.SetSpeed(1.5);
        var buffer = new short[Rate / 2];
        Assert.Equal(buffer.Length, stretcher.Render(buffer));
        Assert.All(buffer.AsSpan(100).ToArray(), s => Assert.Equal(6_000, s));

        mixer.SetMuted(1, true);
        stretcher.Render(buffer); // the 10 ms fade plus the stretcher's read-ahead (< 50 ms) pass through here
        Assert.Equal(buffer.Length, stretcher.Render(buffer));

        Assert.All(buffer, s => Assert.Equal(1_000, s));
        // The ramp itself is monotonic: no sample outside [1000, 6000] was ever produced (no overshoot from the cross-fades).
        mixer.SetMuted(1, false);
        stretcher.Render(buffer);
        var next = new short[Rate / 2];
        stretcher.Render(next);
        Assert.All(next, s => Assert.InRange(s, 1_000, 6_000));
    }

    [Fact]
    public void TheStretcher_OnTheMixer_DoesNotAllocate_AtSpeedsOtherThan1()
    {
        const int Total = 4 * Rate;
        using var mixer = new TrackMixer([TestWav.Reader(WsolaStretcherTests.Voice(Total)), TestWav.Reader(WsolaStretcherTests.Sine(300, Total))]);
        var stretcher = new WsolaStretcher(mixer, Rate);
        var buffer = new short[800];
        mixer.SetMuted(0, false);

        for (var pass = 0; pass < 3; pass++)
        {
            Drain(stretcher, mixer, buffer);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        Drain(stretcher, mixer, buffer);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void Drain(WsolaStretcher stretcher, TrackMixer mixer, short[] buffer)
    {
        stretcher.Seek(0);
        stretcher.SetSpeed(1.25);
        for (var i = 0; i < 30; i++)
        {
            stretcher.Render(buffer);
        }

        mixer.SetMuted(1, true);
        stretcher.SetSpeed(2);
        for (var i = 0; i < 30; i++)
        {
            stretcher.Render(buffer);
        }

        mixer.SetMuted(1, false);
        stretcher.SetSpeed(0.75);
        stretcher.Seek(Rate);
        while (stretcher.Render(buffer) > 0)
        {
        }
    }
}
