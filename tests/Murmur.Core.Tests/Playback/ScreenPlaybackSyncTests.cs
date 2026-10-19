using Murmur.Core.Playback;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Playback;

public sealed class ScreenPlaybackSyncTests
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    // Segment 0: session 2 s .. 12 s; gap 12 .. 20 s; segment 1: 20 s .. 30 s; segment 2 touches it: 30 .. 35 s.
    private static readonly ScreenSegment[] Segments =
    [
        new(1, S(20), S(10)),
        new(0, S(2), S(10)),
        new(2, S(30), S(5)),
    ];

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static (ScreenPlaybackSync Sync, ManualTimeProvider Time) Create(params ScreenSegment[] segments)
    {
        var time = new ManualTimeProvider();
        return (new ScreenPlaybackSync(segments.Length == 0 ? Segments : segments, time: time), time);
    }

    public static TheoryData<double, int> SegmentLookup() => new()
    {
        { 0, -1 },      // before the first segment: screen off
        { 1.999, -1 },
        { 2, 0 },       // the start belongs to the segment
        { 7, 0 },
        { 12, 0 },      // so does the end
        { 12.001, -1 }, // gap
        { 19.5, -1 },
        { 20, 1 },
        { 29.999, 1 },
        { 30, 2 },      // touching segments: the later one
        { 35, 2 },
        { 35.5, -1 },   // after the last one
    };

    [Theory]
    [MemberData(nameof(SegmentLookup))]
    public void SegmentAt_FindsTheSegmentContainingThePosition(double seconds, int expected)
    {
        var (sync, _) = Create();
        Assert.Equal(expected, sync.SegmentAt(S(seconds))?.Index ?? -1);
    }

    [Fact]
    public void Segments_AreSortedByOffset()
    {
        var (sync, _) = Create();
        Assert.Equal([0, 1, 2], sync.Segments.Select(s => s.Index));
    }

    public static TheoryData<double, int, double> OpenTargets() => new()
    {
        { 2, 0, 0 },
        { 7.25, 0, 5.25 },
        { 12, 0, 10 },
        { 20.5, 1, 0.5 },
        { 33, 2, 3 },
    };

    [Theory]
    [MemberData(nameof(OpenTargets))]
    public void Closed_InsideASegment_OpensIt_WithTheTargetTime(double audio, int segment, double target)
    {
        var (sync, _) = Create();
        var command = sync.Update(S(audio), audioPlaying: false, VideoPlayerState.Closed);
        Assert.Equal(new VideoSyncCommand(VideoSyncActions.Open, segment, S(target)), command);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(15)]
    [InlineData(40)]
    public void OutsideEverySegment_ClosesWhatIsOpen_AndThenDoesNothing(double audio)
    {
        var (sync, _) = Create();
        Assert.Equal(VideoSyncActions.Close, sync.Update(S(audio), true, VideoPlayerState.Open(0, S(3), true, S(10))).Actions);
        Assert.Equal(VideoSyncActions.Close, sync.Update(S(audio), true, VideoPlayerState.Opening(1)).Actions); // cancels an open
        Assert.Equal(VideoSyncCommand.Nothing, sync.Update(S(audio), true, VideoPlayerState.Closed));
    }

    [Fact]
    public void WhileOpening_TheSameSegment_NothingIsRequested()
    {
        var (sync, time) = Create();
        Assert.Equal(VideoSyncActions.Open, sync.Update(S(5), true, VideoPlayerState.Closed).Actions);
        for (var i = 0; i < 10; i++)
        {
            time.Advance(33 * Ms);
            var command = sync.Update(S(5) + ((i + 1) * 33 * Ms), true, VideoPlayerState.Opening(0));
            Assert.Equal(VideoSyncActions.None, command.Actions);
        }
    }

    [Fact]
    public void WhileOpening_AnotherSegment_OpensTheRightOne()
    {
        var (sync, _) = Create();
        var command = sync.Update(S(25), false, VideoPlayerState.Opening(0));
        Assert.Equal(new VideoSyncCommand(VideoSyncActions.Open, 1, S(5)), command);
    }

    [Fact]
    public void AnotherSegmentOpen_OpensTheRightOne()
    {
        var (sync, _) = Create();
        Assert.Equal(new VideoSyncCommand(VideoSyncActions.Open, 2, S(1)), sync.Update(S(31), true, VideoPlayerState.Open(1, S(9.9), true, S(10))));
    }

    public static TheoryData<double, double, bool, bool, VideoSyncActions> FirstUpdateAfterOpen() => new()
    {
        // audio s, video s, audio playing, video playing -> actions (segment 0 starts at 2 s; tolerance 150 ms)
        { 2.0, 0.0, false, false, VideoSyncActions.None },
        { 2.149, 0.0, false, false, VideoSyncActions.None },                       // within the tolerance
        { 2.151, 0.0, false, false, VideoSyncActions.Seek },                       // just outside
        { 7.0, 0.0, false, false, VideoSyncActions.Seek },
        { 7.0, 0.0, true, false, VideoSyncActions.Seek | VideoSyncActions.Play },
        { 2.1, 0.0, true, false, VideoSyncActions.Play },
        { 7.0, 5.0, true, true, VideoSyncActions.None },
        { 7.0, 5.0, false, true, VideoSyncActions.Pause },
        { 7.0, 4.0, false, true, VideoSyncActions.Seek | VideoSyncActions.Pause },
        { 11.9, 9.9, true, false, VideoSyncActions.None },                          // within the tolerance of the end: no Play
        { 11.8, 9.8, true, false, VideoSyncActions.Play },
    };

    [Theory]
    [MemberData(nameof(FirstUpdateAfterOpen))]
    public void FirstUpdateAfterOpen_SeeksAtOnce_AndFollowsPlayPause(double audio, double video, bool audioPlaying, bool videoPlaying, VideoSyncActions expected)
    {
        var (sync, _) = Create();
        var command = sync.Update(S(audio), audioPlaying, VideoPlayerState.Open(0, S(video), videoPlaying, S(10)));
        Assert.Equal(expected, command.Actions);
        Assert.Equal(0, command.SegmentIndex);
        if (command.Has(VideoSyncActions.Seek))
        {
            Assert.Equal(S(audio - 2), command.Position);
        }
    }

    [Fact]
    public void Target_IsClampedToTheFileDuration_WhenItIsShorter()
    {
        var (sync, _) = Create();

        // The file is 9.5 s although the segment says 10 s: at 11.8 s the video sits at its end and must neither seek nor
        // restart (a media player plays an ended file from 0).
        var command = sync.Update(S(11.8), true, VideoPlayerState.Open(0, S(9.5), false, S(9.5)));
        Assert.Equal(VideoSyncActions.None, command.Actions);
        Assert.Equal(S(9.5), command.Position);
    }

    [Fact]
    public void Drift_WhilePlaying_SeeksAtMostEvery500ms()
    {
        var (sync, time) = Create();
        var audio = S(5);
        Assert.Equal(VideoSyncActions.Seek, sync.Update(audio, true, VideoPlayerState.Open(0, S(0), true, S(10))).Actions);

        // The video stays 300 ms behind (a lagging decoder): the next seek waits for the interval.
        var seeks = new List<TimeSpan>();
        for (var tick = 1; tick <= 30; tick++)
        {
            time.Advance(50 * Ms);
            audio += 50 * Ms;
            var command = sync.Update(audio, true, VideoPlayerState.Open(0, audio - S(2) - (300 * Ms), true, S(10)));
            if (command.Has(VideoSyncActions.Seek))
            {
                seeks.Add(time.GetUtcNow() - new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            }
        }

        Assert.NotEmpty(seeks);
        Assert.True(seeks[0] >= 500 * Ms, $"first re-seek at {seeks[0]}");
        for (var i = 1; i < seeks.Count; i++)
        {
            Assert.True(seeks[i] - seeks[i - 1] >= 500 * Ms, $"seeks at {seeks[i - 1]} and {seeks[i]}");
        }
    }

    [Fact]
    public void Drift_WithinTolerance_IsLeftAlone()
    {
        var (sync, time) = Create();
        var audio = S(5);
        for (var tick = 0; tick < 100; tick++)
        {
            var command = sync.Update(audio, true, VideoPlayerState.Open(0, audio - S(2) + (tick % 2 == 0 ? 140 : -140) * Ms, true, S(10)));
            Assert.Equal(VideoSyncActions.None, command.Actions);
            time.Advance(33 * Ms);
            audio += 33 * Ms;
        }
    }

    [Fact]
    public void UserSeek_WhilePlaying_SeeksAtOnce_EvenRightAfterAnotherSeek()
    {
        var (sync, time) = Create();
        Assert.Equal(VideoSyncActions.Seek, sync.Update(S(5), true, VideoPlayerState.Open(0, S(0), true, S(10))).Actions);
        time.Advance(33 * Ms);

        // The user clicks a word at 9 s, 33 ms after the last seek.
        var command = sync.Update(S(9), true, VideoPlayerState.Open(0, S(3.03), true, S(10)));
        Assert.Equal(VideoSyncActions.Seek, command.Actions);
        Assert.Equal(S(7), command.Position);
    }

    [Fact]
    public void Paused_SeeksToEveryNewTarget_ButRepeatsTheSameOneOnlyEvery500ms()
    {
        var (sync, time) = Create();
        Assert.Equal(VideoSyncActions.Seek, sync.Update(S(5), false, VideoPlayerState.Open(0, S(0), false, S(10))).Actions);

        // Scrubbing while paused: a new target every tick, each one seeks.
        for (var i = 1; i <= 5; i++)
        {
            time.Advance(33 * Ms);
            var command = sync.Update(S(5 + i), false, VideoPlayerState.Open(0, S(3), false, S(10)));
            Assert.Equal(VideoSyncActions.Seek, command.Actions);
            Assert.Equal(S(3 + i), command.Position);
        }

        // The decoder does not get there: the same target is asked again only after 500 ms.
        var repeats = 0;
        for (var i = 0; i < 30; i++)
        {
            time.Advance(50 * Ms);
            if (sync.Update(S(10), false, VideoPlayerState.Open(0, S(3), false, S(10))).Has(VideoSyncActions.Seek))
            {
                repeats++;
            }
        }

        Assert.InRange(repeats, 2, 3);
    }

    [Fact]
    public void Unplayable_Segment_IsShownAsScreenOff()
    {
        var (sync, _) = Create();
        sync.MarkUnplayable(0);
        Assert.True(sync.IsUnplayable(0));
        Assert.False(sync.IsUnplayable(1));
        Assert.Equal(VideoSyncCommand.Nothing, sync.Update(S(5), true, VideoPlayerState.Closed));
        Assert.Equal(VideoSyncActions.Close, sync.Update(S(5), true, VideoPlayerState.Opening(0)).Actions);
        Assert.Equal(VideoSyncActions.Open, sync.Update(S(25), true, VideoPlayerState.Closed).Actions);
    }

    [Fact]
    public void NoSegments_NeverOpens()
    {
        var sync = new ScreenPlaybackSync([], time: new ManualTimeProvider());
        Assert.Equal(VideoSyncCommand.Nothing, sync.Update(S(5), true, VideoPlayerState.Closed));
        Assert.Null(sync.SegmentAt(S(0)));
    }

    [Fact]
    public void InvalidOptions_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenPlaybackSync(Segments, new ScreenPlaybackSyncOptions { Tolerance = -Ms }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenPlaybackSync(Segments, new ScreenPlaybackSyncOptions { MinSeekInterval = -Ms }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenPlaybackSync(Segments, new ScreenPlaybackSyncOptions { MaxSeekLead = -Ms }));
        Assert.Throws<ArgumentNullException>(() => new ScreenPlaybackSync(null!));
    }

    [Fact]
    public void Update_DoesNotAllocate()
    {
        var (sync, time) = Create();
        var states = new[]
        {
            VideoPlayerState.Closed,
            VideoPlayerState.Opening(0),
            VideoPlayerState.Open(0, S(1), true, S(10)),
            VideoPlayerState.Open(1, S(1), false, S(10)),
        };

        void Run()
        {
            var audio = TimeSpan.Zero;
            for (var i = 0; i < 2_000; i++)
            {
                time.Advance(16 * Ms);
                audio += 20 * Ms;
                sync.Update(audio, i % 3 != 0, states[i % states.Length]);
            }
        }

        Run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Simulation_TwoSegmentsWithGaps_PlaysThrough_InSync()
    {
        // Session 0..40 s; screen 3..13 s (segment 0) and 20..32 s (segment 1). Opens take 120 ms, seeks 80 ms.
        var segments = new[] { new ScreenSegment(0, S(3), S(10)), new ScreenSegment(1, S(20), S(12)) };
        var sim = new Simulation(segments, openLatency: 120 * Ms, seekLatency: 80 * Ms);
        sim.Audio.Play();
        sim.Run(S(40));

        Assert.Equal([0, 1], sim.Video.Opened);
        Assert.Equal(2, sim.Closes); // after each segment
        Assert.True(sim.Video.Seeks.Count <= 4, $"{sim.Video.Seeks.Count} seeks");

        // In sync (within the tolerance) for all but the first moments after each open / seek.
        Assert.True(sim.OutOfSyncTicks <= 20, $"{sim.OutOfSyncTicks} ticks out of sync");
        Assert.True(sim.PlaceholderTicks > 0);
    }

    [Fact]
    public void Simulation_UserSeeksAcrossSegments_FollowTheAudio()
    {
        var segments = new[] { new ScreenSegment(0, S(3), S(10)), new ScreenSegment(1, S(20), S(12)) };
        var sim = new Simulation(segments, openLatency: 100 * Ms, seekLatency: 50 * Ms);
        sim.Audio.Play();
        sim.Run(S(2));                    // 0 .. 2 s: placeholder
        Assert.True(sim.Video.State.IsClosed);

        sim.Audio.Seek(S(25));            // into segment 1
        sim.Run(S(1));
        Assert.Equal(1, sim.Video.State.SegmentIndex);
        Assert.InRange(sim.Video.Position, S(5.6), S(6.2));
        Assert.True(sim.Video.State.IsPlaying);

        sim.Audio.Seek(S(5));             // back into segment 0
        sim.Run(S(1));
        Assert.Equal(0, sim.Video.State.SegmentIndex);
        Assert.InRange(sim.Video.Position, S(2.6), S(3.2));

        sim.Audio.Seek(S(15));            // into the gap
        sim.Run(S(0.5));
        Assert.True(sim.Video.State.IsClosed);

        sim.Audio.Pause();
        sim.Audio.Seek(S(22));            // paused, into segment 1: open and show the frame at 2 s
        sim.Run(S(0.5));
        Assert.Equal(1, sim.Video.State.SegmentIndex);
        Assert.False(sim.Video.State.IsPlaying);
        Assert.Equal(S(2), sim.Video.Position);

        sim.Audio.Seek(S(7));             // paused, back into segment 0
        sim.Run(S(0.5));
        Assert.Equal(0, sim.Video.State.SegmentIndex);
        Assert.Equal(S(4), sim.Video.Position);
        Assert.Equal([1, 0, 1, 0], sim.Video.Opened);
    }

    [Fact]
    public void Simulation_DecoderThatNeverAdvances_GetsBoundedSeeks()
    {
        var segments = new[] { new ScreenSegment(0, S(0), S(60)) };
        var sim = new Simulation(segments, openLatency: 50 * Ms, seekLatency: 50 * Ms, frozen: true); // the picture never moves
        sim.Audio.Play();
        sim.Run(S(10));

        // At most one seek per 500 ms (plus the first one), never a storm.
        Assert.InRange(sim.Video.Seeks.Count, 2, 21);
        Assert.True(sim.Video.Seeks.Count <= 7, $"{sim.Video.Seeks.Count} seeks: a pending seek waits up to 2 s");
        for (var i = 1; i < sim.Video.Seeks.Count; i++)
        {
            Assert.True(sim.Video.Seeks[i].At - sim.Video.Seeks[i - 1].At >= 500 * Ms);
        }

        Assert.True(sim.Sync.SeekLead <= sim.Sync.Options.MaxSeekLead);
    }

    [Fact]
    public void Simulation_SlowSeekingDecoder_LearnsALead_AndSettles()
    {
        // Every seek costs 300 ms during which the picture stands still: without a lead the video would be 300 ms behind
        // after every seek and seek again forever.
        var segments = new[] { new ScreenSegment(0, S(0), S(60)) };
        var sim = new Simulation(segments, openLatency: 50 * Ms, seekLatency: 300 * Ms);
        sim.Audio.Play();
        sim.Audio.Seek(S(10));
        sim.Run(S(20));

        Assert.InRange(sim.Sync.SeekLead, 200 * Ms, 400 * Ms);
        Assert.True(sim.Video.Seeks.Count <= 4, $"{sim.Video.Seeks.Count} seeks");
        var lastSeek = sim.Video.Seeks[^1].At;
        Assert.True(lastSeek < S(3), $"still seeking at {lastSeek}");
        Assert.True(Math.Abs((sim.Audio.Position - sim.Video.Position).TotalMilliseconds) <= 150);
    }

    [Fact]
    public void Simulation_SeekSlowerThanTheInterval_IsWaitedFor_NotRepeated()
    {
        // Every seek takes 700 ms (more than MinSeekInterval): the sync must wait for it to land instead of seeking twice
        // a second forever, then learn the lead and settle.
        var segments = new[] { new ScreenSegment(0, S(0), S(60)) };
        var sim = new Simulation(segments, openLatency: 50 * Ms, seekLatency: 700 * Ms);
        sim.Audio.Play();
        sim.Audio.Seek(S(10));
        sim.Run(S(25));

        var seeks = sim.Video.Seeks;
        Assert.True(seeks.Count <= 7, $"{seeks.Count} seeks: {string.Join(", ", seeks.Select(x => x.At.TotalSeconds.ToString("F2")))}; lead {sim.Sync.SeekLead}");
        for (var i = 1; i < seeks.Count; i++)
        {
            Assert.True(seeks[i].At - seeks[i - 1].At >= 700 * Ms, $"seeks at {seeks[i - 1].At} and {seeks[i].At}");
        }

        Assert.True(seeks[^1].At < S(12), $"still seeking at {seeks[^1].At}");
        Assert.InRange(sim.Sync.SeekLead, 500 * Ms, 900 * Ms);
        Assert.True(Math.Abs((sim.Audio.Position - sim.Video.Position).TotalMilliseconds) <= 150);
    }

    [Fact]
    public void Simulation_VariableSeekLatency_GetsBoundedSeeks_AndSettles()
    {
        // Seek latency alternating between 400 and 100 ms: the damped lead settles between them instead of flipping.
        var segments = new[] { new ScreenSegment(0, S(0), S(60)) };
        var sim = new Simulation(segments, openLatency: 50 * Ms, seekLatency: TimeSpan.Zero, seekLatencies: [400 * Ms, 100 * Ms]);
        sim.Audio.Play();
        sim.Audio.Seek(S(10));
        sim.Run(S(30));

        var seeks = sim.Video.Seeks;
        Assert.True(seeks.Count <= 8, $"{seeks.Count} seeks: {string.Join(", ", seeks.Select(x => x.At.TotalSeconds.ToString("F2")))}; lead {sim.Sync.SeekLead}");
        Assert.True(seeks[^1].At < S(15), $"still seeking at {seeks[^1].At}");
        Assert.InRange(sim.Sync.SeekLead, 100 * Ms, 400 * Ms);
        Assert.True(Math.Abs((sim.Audio.Position - sim.Video.Position).TotalMilliseconds) <= 150);
    }

    /// <summary>A UI timer at 30 Hz driving a fake audio clock and a fake video player through the sync.</summary>
    private sealed class Simulation
    {
        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(33);

        public Simulation(ScreenSegment[] segments, TimeSpan openLatency, TimeSpan seekLatency, bool frozen = false, TimeSpan[]? seekLatencies = null)
        {
            Time = new ManualTimeProvider();
            Sync = new ScreenPlaybackSync(segments, time: Time);
            Audio = new FakeAudio(S(40));
            Video = new FakeVideo(Time, segments, openLatency, seekLatencies ?? [seekLatency], frozen);
        }

        public ManualTimeProvider Time { get; }

        public ScreenPlaybackSync Sync { get; }

        public FakeAudio Audio { get; }

        public FakeVideo Video { get; }

        public int Closes { get; private set; }

        public int OutOfSyncTicks { get; private set; }

        public int PlaceholderTicks { get; private set; }

        public void Run(TimeSpan duration)
        {
            for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += Tick)
            {
                var command = Sync.Update(Audio.Position, Audio.IsPlaying, Video.State);
                if (command.Has(VideoSyncActions.Close))
                {
                    Video.Close();
                    Closes++;
                }

                if (command.Has(VideoSyncActions.Open))
                {
                    Video.Open(command.SegmentIndex);
                }

                if (command.Has(VideoSyncActions.Seek))
                {
                    Video.Seek(command.Position);
                }

                if (command.Has(VideoSyncActions.Play))
                {
                    Video.Play();
                }

                if (command.Has(VideoSyncActions.Pause))
                {
                    Video.Pause();
                }

                Measure();
                Time.Advance(Tick);
                Audio.Advance(Tick);
                Video.Advance(Tick);
            }
        }

        private void Measure()
        {
            var segment = Sync.SegmentAt(Audio.Position);
            if (segment is null)
            {
                PlaceholderTicks++;
                if (!Video.State.IsClosed)
                {
                    OutOfSyncTicks++;
                }

                return;
            }

            var target = Audio.Position - segment.Offset;
            if (Video.State.SegmentIndex != segment.Index || Video.State.IsOpening || (Video.Position - target).Duration() > 150 * Ms)
            {
                OutOfSyncTicks++;
            }
        }
    }

    private sealed class FakeAudio(TimeSpan duration)
    {
        public TimeSpan Position { get; private set; }

        public bool IsPlaying { get; private set; }

        public void Play() => IsPlaying = true;

        public void Pause() => IsPlaying = false;

        public void Seek(TimeSpan position) => Position = position;

        public void Advance(TimeSpan by)
        {
            if (IsPlaying)
            {
                Position += by;
                if (Position >= duration)
                {
                    Position = duration;
                    IsPlaying = false;
                }
            }
        }
    }

    /// <summary>
    /// Media-engine-like player: OpenAsync completes after a latency (paused at 0); Seek sets the position at once but
    /// the picture stands still for the seek latency; at its end playback stops. A frozen one never advances (a decoder
    /// that cannot keep up at all).
    /// </summary>
    private sealed class FakeVideo(ManualTimeProvider time, ScreenSegment[] segments, TimeSpan openLatency, TimeSpan[] seekLatencies, bool frozen)
    {
        private int _segment = VideoPlayerState.NoSegment;
        private TimeSpan _openRemaining;
        private TimeSpan _stallRemaining;
        private bool _playing;
        private TimeSpan _duration;

        public List<int> Opened { get; } = [];

        public List<(TimeSpan At, TimeSpan Target)> Seeks { get; } = [];

        public TimeSpan Position { get; private set; }

        public VideoPlayerState State => _segment == VideoPlayerState.NoSegment ? VideoPlayerState.Closed
            : _openRemaining > TimeSpan.Zero ? VideoPlayerState.Opening(_segment)
            : VideoPlayerState.Open(_segment, Position, _playing, _duration);

        public void Open(int segment)
        {
            Opened.Add(segment);
            _segment = segment;
            _openRemaining = openLatency;
            _duration = segments.Single(s => s.Index == segment).Duration;
            Position = TimeSpan.Zero;
            _playing = false;
            _stallRemaining = TimeSpan.Zero;
        }

        public void Close()
        {
            _segment = VideoPlayerState.NoSegment;
            _playing = false;
        }

        public void Seek(TimeSpan position)
        {
            Assert.False(State.IsOpening || State.IsClosed, "seek on a player that is not open");
            Seeks.Add((time.GetUtcNow() - new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), position));
            Position = position < TimeSpan.Zero ? TimeSpan.Zero : position > _duration ? _duration : position;
            _stallRemaining = seekLatencies[(Seeks.Count - 1) % seekLatencies.Length];
        }

        public void Play()
        {
            Assert.False(State.IsOpening || State.IsClosed, "play on a player that is not open");
            _playing = true;
        }

        public void Pause() => _playing = false;

        public void Advance(TimeSpan by)
        {
            if (_segment == VideoPlayerState.NoSegment)
            {
                return;
            }

            if (_openRemaining > TimeSpan.Zero)
            {
                _openRemaining -= by;
                if (_openRemaining <= TimeSpan.Zero)
                {
                    _openRemaining = TimeSpan.Zero;
                }

                return;
            }

            if (_stallRemaining > TimeSpan.Zero)
            {
                _stallRemaining -= by;
                return;
            }

            if (_playing && !frozen)
            {
                Position += by;
                if (Position >= _duration)
                {
                    Position = _duration;
                    _playing = false;
                }
            }
        }
    }
}
