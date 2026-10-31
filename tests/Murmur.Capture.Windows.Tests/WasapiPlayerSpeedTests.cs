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
    public void PlayedMediaFrame_RoundsAndClampsTheHeardMedia()
    {
        Assert.Equal(4_200, PlaybackTiming.PlayedMediaFrame(1_000, 4_199.6, 100_000));
        Assert.Equal(1_000, PlaybackTiming.PlayedMediaFrame(1_000, 600, 100_000)); // never before where it started
        Assert.Equal(100_000, PlaybackTiming.PlayedMediaFrame(1_000, 120_000, 100_000)); // never past the end
    }

    [Theory]
    [InlineData(2.0, 0.75)]
    [InlineData(1.5, 2.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(1.0, 2.0)]
    [InlineData(0.5, 1.0)]
    [InlineData(0.75, 2.0)]
    public void TheHeardPosition_IsMonotonicAndContinuous_AcrossAChangeOfSpeed(double before, double after)
    {
        // A device buffer of 50 ms drained at 16 kHz in 10 ms steps, refilled to 50 ms; the position is read the way the engine does.
        var source = new ArraySource(WsolaStretcherTests.Voice(30 * Rate));
        var stretcher = new WsolaStretcher(source, Rate);
        stretcher.SetSpeed(before);
        const int Step = 160;
        const int Buffer = 800;
        var block = new short[Buffer];
        long played = 0;
        var previous = 0d;
        var largest = 0d;
        var smallest = double.MaxValue;
        for (var step = 0; step < 400; step++)
        {
            if (step == 150)
            {
                stretcher.SetSpeed(after);
            }

            var padding = stretcher.OutputFrames - played;
            stretcher.Render(block.AsSpan(0, (int)(Buffer - padding)));
            played += Step;
            var heard = stretcher.MediaAtOutput(played);
            if (step >= 20)
            {
                var delta = heard - previous;
                largest = Math.Max(largest, delta);
                smallest = Math.Min(smallest, delta);
            }

            previous = heard;
        }

        // Each 10 ms step moves the position by 10 ms x the speed there: never back, never more than a few ms off that.
        var low = Step * Math.Min(before, after);
        var high = Step * Math.Max(before, after);
        Assert.True(smallest >= low - 3, $"smallest step {smallest} < {low}");
        Assert.True(largest <= high + 3, $"largest step {largest} > {high}");
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void AMuteAtASlowSpeed_IsNotFollowedByTheOldMixAgain(double speed)
    {
        // Track 0 = 1000, track 1 = 5000: 6000 until track 1 is muted, 1000 after. At a slow speed the hops start behind what was
        // read ahead, which was mixed before the mute. Tried at many phases of the hop.
        for (var phase = 0; phase < 700; phase += 29)
        {
            using var mixer = new TrackMixer([TestWav.Reader(TestWav.Constant(30 * Rate, 1_000)), TestWav.Reader(TestWav.Constant(30 * Rate, 5_000))]);
            var stretcher = new WsolaStretcher(mixer, Rate);
            stretcher.SetSpeed(speed);
            stretcher.Render(new short[Rate + phase]);

            mixer.SetMuted(1, true);
            var after = new short[Rate];
            Assert.Equal(after.Length, stretcher.Render(after));

            var settled = Array.FindIndex(after, s => s == 1_000);
            Assert.InRange(settled, 0, 1_600); // the queued hop plus the 10 ms fade, within 100 ms
            Assert.True(after.AsSpan(settled).ToArray().All(s => s == 1_000), $"6000 came back after the mute (phase {phase}, speed {speed})");
            var step = 0;
            for (var i = 1; i < after.Length; i++)
            {
                step = Math.Max(step, Math.Abs(after[i] - after[i - 1]));
            }

            Assert.True(step <= 40, $"the fade must stay a ramp (5000 over 160 frames = 31 per frame), largest step {step} (phase {phase})");
        }
    }

    /// <summary>6000 until <see cref="ChangeVersion"/> is bumped, 1000 for every frame read after: the instant of a mute, without a fade.</summary>
    private sealed class MuteSwitchSource : IPcmSource
    {
        public long TotalFrames => 60 * Rate;

        public long Position { get; private set; }

        public int ChangeVersion { get; private set; }

        public int Rewinds { get; private set; }

        public void Mute() => ChangeVersion++;

        public int Render(Span<short> destination)
        {
            var count = (int)Math.Min(destination.Length, TotalFrames - Position);
            destination[..count].Fill((short)(ChangeVersion == 0 ? 6_000 : 1_000));
            Position += count;
            return count;
        }

        public void Seek(long frame) => Position = Math.Clamp(frame, 0, TotalFrames);

        public void Rewind(long frame)
        {
            Rewinds++;
            Seek(frame);
        }
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.5)]
    public void AChangeOfTheMix_DropsTheReadAhead_SoOnlyTheQueuedHopStillHasTheOldMix(double speed)
    {
        for (var phase = 0; phase < 700; phase += 41)
        {
            var source = new MuteSwitchSource();
            var stretcher = new WsolaStretcher(source, Rate);
            stretcher.SetSpeed(speed);
            stretcher.Render(new short[Rate + phase]);

            source.Mute();
            var after = new short[Rate];
            Assert.Equal(after.Length, stretcher.Render(after));

            Assert.Equal(1, source.Rewinds);
            var first = Array.FindIndex(after, s => s == 1_000);
            Assert.InRange(first, 0, 320); // at most the hop that was being played out
            Assert.True(after.AsSpan(first).ToArray().All(s => s == 1_000), $"the old mix came back (phase {phase}, speed {speed})");
        }
    }

    [Fact]
    public void AnUnmute_AtASlowSpeed_BringsTheTrackBack_WithoutAStep()
    {
        using var mixer = new TrackMixer([TestWav.Reader(TestWav.Constant(30 * Rate, 1_000)), TestWav.Reader(TestWav.Constant(30 * Rate, 5_000))]);
        mixer.SetMuted(1, true);
        mixer.MutedMask = 0b10; // as the player sets it for a new mixer: at once, no fade
        var stretcher = new WsolaStretcher(mixer, Rate);
        stretcher.SetSpeed(0.75);
        var block = new short[Rate / 5];
        stretcher.Render(block);
        Assert.Equal(1_000, block[^1]);

        mixer.SetMuted(1, false);
        var after = new short[Rate];
        stretcher.Render(after);

        Assert.Equal(6_000, after[^1]);
        var step = 0;
        for (var i = 1; i < after.Length; i++)
        {
            step = Math.Max(step, Math.Abs(after[i] - after[i - 1]));
        }

        Assert.True(step <= 40, $"largest step {step}");
    }

    [Fact]
    public void TheMixerRewind_PutsTheFadesBack()
    {
        using var mixer = new TrackMixer([TestWav.Reader(TestWav.Constant(Rate, 1_000)), TestWav.Reader(TestWav.Constant(Rate, 5_000))]);
        var output = new short[400];
        mixer.Render(output);
        mixer.SetMuted(1, true);
        mixer.Render(output); // 400 frames: the 160-frame fade is over by the end
        Assert.Equal(1_000, output[^1]);

        mixer.Rewind(400); // back to where the mute started: the fade runs again from there
        var again = new short[400];
        mixer.Render(again);

        Assert.Equal(output, again);
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
