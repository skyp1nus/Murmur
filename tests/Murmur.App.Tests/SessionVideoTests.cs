using Murmur.App.Resources;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Capture;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

/// <summary>The screen recording in the session view model: which segment is open, seeks, play / pause, failures.</summary>
public sealed class SessionVideoTests : IDisposable
{
    private readonly SessionFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>Segment 0 covers 0..10 s, segment 1 covers 20..30 s of the session; 10..20 s the screen was off.</summary>
    private static readonly ScreenSegment[] TwoSegments = [new(0, TimeSpan.Zero, TimeSpan.FromSeconds(10)), new(1, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10))];

    private (SessionViewModel Vm, FakeVideoPlayer Video, FakeVideoPlayback Playback) Create(
        ScreenSegment[]? segments = null,
        TimeSpan? fileDuration = null,
        bool files = true,
        bool supported = true)
    {
        segments ??= TwoSegments;
        var record = _fixture.Record with { ScreenSegments = segments };
        if (files)
        {
            foreach (var segment in segments)
            {
                File.WriteAllBytes(_fixture.Files.ScreenSegment(segment.Index), [0]);
            }
        }

        var video = new FakeVideoPlayer(fileDuration ?? S(10));
        var playback = new FakeVideoPlayback(video, supported);
        var vm = new SessionViewModel(record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(), videoPlayback: playback);
        return (vm, video, playback);
    }

    [Fact]
    public async Task TwoSegmentsAndAGap_OpenTheFirstAtStart_ShowThePlaceholderInTheGap_AndOpenTheSecondAfterIt()
    {
        var (vm, video, playback) = Create();

        await vm.LoadAsync();

        Assert.True(vm.HasVideo);
        Assert.Same(video, vm.VideoPlayer);
        Assert.Equal(1, playback.Created);
        Assert.Equal([_fixture.Files.ScreenSegment(0)], video.Opens);
        Assert.True(vm.IsVideoOpen);
        Assert.False(vm.ShowVideoPlaceholder);
        Assert.Equal("1920×1080 · H.264", vm.VideoInfoText);

        vm.SeekTo(S(15));

        Assert.Equal(1, video.Closes);
        Assert.False(vm.IsVideoOpen);
        Assert.True(vm.ShowVideoPlaceholder);
        Assert.Equal(Strings.Session_VideoOff, vm.VideoPlaceholderText);

        vm.SeekTo(S(25));

        Assert.Equal([_fixture.Files.ScreenSegment(0), _fixture.Files.ScreenSegment(1)], video.Opens);
        Assert.True(vm.IsVideoOpen);
        Assert.Equal([S(5)], video.Seeks); // media time = session time - the segment's offset
        Assert.Equal(S(5), video.Position);
    }

    [Fact]
    public async Task WhileTheOpenIsInFlight_ThePlaceholderSaysLoading_AndNoSecondOpenIsAsked()
    {
        var (vm, video, _) = Create();
        video.OpenGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await vm.LoadAsync();
        vm.Tick();
        vm.Tick();

        Assert.Single(video.Opens);
        Assert.False(vm.IsVideoOpen);
        Assert.Equal(Strings.Session_Loading, vm.VideoPlaceholderText);

        video.OpenGate.SetResult();
        await Until(() => vm.IsVideoOpen);

        Assert.Single(video.Opens);
        Assert.Equal("1920×1080 · H.264", vm.VideoInfoText);
    }

    [Fact]
    public async Task AWordClickOnAPausedSession_SeeksTheVideo_AndThenPlaysIt()
    {
        _fixture.UseShortTranscript();
        var (vm, video, _) = Create([new ScreenSegment(0, TimeSpan.Zero, TimeSpan.FromHours(1))], TimeSpan.FromHours(1));
        await vm.LoadAsync();
        Assert.True(vm.IsVideoOpen);
        Assert.Empty(video.Seeks);

        vm.Segments[0].SeekWordCommand.Execute(2); // "починаємо", 10 s + 2 * 0.5 s

        Assert.Equal([S(11)], video.Seeks);
        Assert.Equal(S(11), video.Position);
        Assert.Equal(1, _fixture.Player.Plays);
        Assert.Equal(1, video.Plays);
        Assert.True(video.IsPlaying);
    }

    [Fact]
    public async Task ASeekOnThePausedWaveform_ShowsTheRightFrame_WithoutPlaying()
    {
        var (vm, video, _) = Create([new ScreenSegment(0, TimeSpan.Zero, TimeSpan.FromHours(1))], TimeSpan.FromHours(1));
        await vm.LoadAsync();
        var before = vm.VideoRevision;

        vm.SeekToFraction(0.5);

        Assert.Equal([TimeSpan.FromMinutes(30)], video.Seeks);
        Assert.Equal(0, video.Plays);
        Assert.False(video.IsPlaying);
        Assert.True(vm.VideoRevision > before, "the surface must be woken to fetch the picture");
    }

    [Fact]
    public async Task PlayAndPause_FollowTheAudio()
    {
        var (vm, video, _) = Create();
        await vm.LoadAsync();

        vm.PlayPauseCommand.Execute(null);

        Assert.Equal(PlaybackState.Playing, _fixture.Player.State);
        Assert.Equal(1, video.Plays);
        Assert.True(video.IsPlaying);

        vm.PlayPauseCommand.Execute(null);

        Assert.Equal(1, video.Pauses);
        Assert.False(video.IsPlaying);
    }

    [Fact]
    public async Task AudioThatDriftsAwayFromTheVideo_IsFollowedBySeeksOnTheTick()
    {
        var (vm, video, _) = Create([new ScreenSegment(0, TimeSpan.Zero, TimeSpan.FromHours(1))], TimeSpan.FromHours(1));
        await vm.LoadAsync();
        vm.PlayPauseCommand.Execute(null);
        Assert.Empty(video.Seeks);

        _fixture.Player.Position = S(40); // far from where the video is (it did not advance)
        vm.Tick();

        Assert.Single(video.Seeks);
        Assert.InRange(video.Seeks[0], S(40), S(41)); // lands a learned lead ahead of the audio, none yet
    }

    [Fact]
    public async Task AnOpenThatFails_ShowsAPlaceholderWithANote_AndTheAudioKeepsPlaying()
    {
        var (vm, video, _) = Create();
        video.OpenFailure = new IOException("no decoder");

        await vm.LoadAsync();
        vm.PlayPauseCommand.Execute(null);
        for (var i = 0; i < 5; i++)
        {
            vm.Tick();
        }

        Assert.False(vm.IsVideoOpen);
        Assert.Equal(Strings.Session_VideoUnplayable, vm.VideoPlaceholderText);
        Assert.Single(video.Opens); // never asked again
        Assert.Equal(PlaybackState.Playing, _fixture.Player.State);
        Assert.Equal(0, _fixture.Player.Pauses);

        // The next segment is still tried, and the gap shows the plain placeholder.
        vm.SeekTo(S(15));
        Assert.Equal(Strings.Session_VideoOff, vm.VideoPlaceholderText);
        vm.SeekTo(S(25));
        Assert.Equal(2, video.Opens.Count);
    }

    [Fact]
    public async Task AMissingFile_IsUnplayable_WithoutAskingThePlayer()
    {
        var (vm, video, _) = Create(files: false);

        await vm.LoadAsync();

        Assert.Empty(video.Opens);
        Assert.False(vm.IsVideoOpen);
        Assert.Equal(Strings.Session_VideoUnplayable, vm.VideoPlaceholderText);
        Assert.True(vm.HasVideo);
        Assert.True(vm.CanPlay);
    }

    [Fact]
    public async Task APlayerThatReportsFailure_ClosesTheSegment_AndMarksItUnplayable()
    {
        var (vm, video, _) = Create();
        await vm.LoadAsync();
        vm.PlayPauseCommand.Execute(null);
        Assert.True(vm.IsVideoOpen);

        video.RaiseFailed("device lost");
        vm.Tick();

        Assert.False(vm.IsVideoOpen);
        Assert.Equal(Strings.Session_VideoUnplayable, vm.VideoPlaceholderText);
        Assert.Single(video.Opens);
        Assert.Equal(PlaybackState.Playing, _fixture.Player.State);
    }

    [Fact]
    public async Task APlayerThatThrows_NeverBreaksTheTick()
    {
        var (vm, video, _) = Create([new ScreenSegment(0, TimeSpan.Zero, TimeSpan.FromHours(1))], TimeSpan.FromHours(1));
        await vm.LoadAsync();
        video.Dispose();
        video.ThrowOnSeek = true;

        var exception = Record.Exception(() => vm.SeekToFraction(0.5));

        Assert.Null(exception);
        Assert.False(vm.IsVideoOpen);
        Assert.Equal(Strings.Session_VideoUnplayable, vm.VideoPlaceholderText);
    }

    [Fact]
    public async Task Dispose_ClosesAndDisposesThePlayer_AndCancelsAnOpenInFlight()
    {
        var (vm, video, _) = Create();
        await vm.LoadAsync();

        vm.Dispose();

        Assert.True(video.Disposed);
        Assert.Null(vm.VideoPlayer);
        Assert.False(vm.HasVideo);

        var (pending, slow, _) = CreatePending();
        await pending.LoadAsync();
        pending.Dispose();
        slow.OpenGate!.SetResult();
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.True(slow.Disposed);
        Assert.False(pending.IsVideoOpen);
    }

    private (SessionViewModel Vm, FakeVideoPlayer Video, FakeVideoPlayback Playback) CreatePending()
    {
        var created = Create();
        created.Video.OpenGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return created;
    }

    [Fact]
    public async Task ChangingTheSession_DisposesThePreviousPlayer_AndGivesTheNextOneItsOwn()
    {
        var recorder = new FakeRecorder();
        var capture = new FakeCapture();
        var store = new FakeSessionStore { Root = Path.Combine(_fixture.Directory, "store") };
        var first = Sessions.Make("Перша", UiHarness.Now.AddHours(-2), TimeSpan.FromMinutes(5), screen: true);
        var second = Sessions.Make("Друга", UiHarness.Now.AddHours(-1), TimeSpan.FromMinutes(5), screen: true);
        store.Add(first);
        store.Add(second);
        foreach (var id in new[] { first.Id, second.Id })
        {
            var files = store.GetFiles(id);
            Directory.CreateDirectory(files.Directory);
            File.WriteAllBytes(files.MicrophoneAudio, [0]);
            File.WriteAllBytes(files.SystemAudio, [0]);
        }

        var playback = new FakeVideoPlayback(() => new FakeVideoPlayer(S(10)));
        var services = new AppServices(
            recorder,
            capture,
            store,
            new TestTime(UiHarness.Now),
            Transcripts: _fixture.Transcripts,
            Playback: new FixedAudioPlayback(_fixture.Player),
            Processing: _fixture.Processing,
            VideoPlayback: playback);
        var shell = ShellViewModel.Create(services);

        shell.OpenSession(first);
        await shell.SessionLoadTask;
        var firstPlayer = Assert.Single(playback.Players);
        Assert.False(firstPlayer.Disposed);

        shell.OpenSession(second);
        await shell.SessionLoadTask;

        Assert.True(firstPlayer.Disposed);
        Assert.Equal(2, playback.Created);
        Assert.False(playback.Players[1].Disposed);

        shell.ShowLibrary();

        Assert.True(playback.Players[1].Disposed);
    }

    [Fact]
    public async Task WithoutAVideoPlayback_AnUnsupportedOne_OrNoSegments_ThereIsNoVideo()
    {
        var withoutPlayback = new SessionViewModel(_fixture.Record with { ScreenSegments = TwoSegments }, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action());
        await withoutPlayback.LoadAsync();

        var (unsupported, unsupportedPlayer, unsupportedPlayback) = Create(supported: false);
        await unsupported.LoadAsync();

        var (noSegments, _, noSegmentsPlayback) = Create(segments: []);
        await noSegments.LoadAsync();

        Assert.All([withoutPlayback, unsupported, noSegments], vm =>
        {
            Assert.False(vm.HasVideo);
            Assert.Null(vm.VideoPlayer);
            Assert.False(vm.IsVideoOpen);
        });
        Assert.Equal(0, unsupportedPlayback.Created);
        Assert.Equal(0, noSegmentsPlayback.Created);
        Assert.Empty(unsupportedPlayer.Opens);
    }

    [Fact]
    public async Task WithoutAudio_ThereIsNoVideo()
    {
        using var silent = new SessionFixture(withAudio: false);
        var video = new FakeVideoPlayer(S(10));
        var vm = new SessionViewModel(silent.Record with { ScreenSegments = TwoSegments }, silent.Files, silent.Transcripts, silent.Player, silent.Processing, silent.Time, action => action(), videoPlayback: new FakeVideoPlayback(video));

        await vm.LoadAsync();

        Assert.False(vm.HasVideo);
        Assert.Empty(video.Opens);
    }

    [Fact]
    public async Task AScrubOnTheWaveform_MovesTheVideoOnlyWhenItEnds()
    {
        var (vm, video, _) = Create([new ScreenSegment(0, TimeSpan.Zero, TimeSpan.FromHours(1))], TimeSpan.FromHours(1));
        await vm.LoadAsync();

        vm.BeginScrub();
        vm.ScrubTo(0.25);
        vm.ScrubTo(0.5);
        vm.Tick();

        Assert.Empty(video.Seeks);

        vm.EndScrub(0.5);

        Assert.Equal([TimeSpan.FromMinutes(30)], video.Seeks);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Timed out waiting for the condition.");
    }

    private sealed class FixedAudioPlayback(FakeAudioPlayer player) : IAudioPlayback
    {
        public IAudioPlayer CreatePlayer() => player;
    }
}
