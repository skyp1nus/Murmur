using Murmur.Core.Capture;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Capture;

public sealed class ClockAudioPlayerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDirectory _dir = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<PlaybackState> _events = [];

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void NewPlayer_IsClosedAndRejectsTransport()
    {
        using var player = new ClockAudioPlayer(_time);

        Assert.Equal(PlaybackState.Closed, player.State);
        Assert.Equal(TimeSpan.Zero, player.Duration);
        Assert.Equal(TimeSpan.Zero, player.Position);
        Assert.Throws<InvalidOperationException>(player.Play);
        Assert.Throws<InvalidOperationException>(player.Pause);
        Assert.Throws<InvalidOperationException>(() => player.Seek(TimeSpan.Zero));
    }

    [Fact]
    public async Task Open_TakesTheLongestTrackAndIsPausedAtZero()
    {
        using var player = await OpenAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3.5));

        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.Equal(TimeSpan.FromSeconds(3.5), player.Duration);
        Assert.Equal(TimeSpan.Zero, player.Position);
        Assert.Equal([PlaybackState.Paused], _events);
        Assert.Equal(2, player.Tracks.Count);
    }

    [Fact]
    public async Task Open_FailuresLeaveThePlayerUnchanged()
    {
        using var player = new ClockAudioPlayer(_time);
        var junk = _dir.File("junk.wav");
        File.WriteAllBytes(junk, new byte[64]);

        await Assert.ThrowsAsync<ArgumentException>(() => player.OpenAsync([], CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<FileNotFoundException>(() => player.OpenAsync([_dir.File("missing.wav")], CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => player.OpenAsync([junk], CancellationToken.None).AsTask());

        Assert.Equal(PlaybackState.Closed, player.State);
    }

    [Fact]
    public async Task Play_AdvancesWithTheClock_PauseHolds()
    {
        using var player = await OpenAsync(TimeSpan.FromSeconds(10));

        player.Play();
        _time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(TimeSpan.FromSeconds(1.5), player.Position);

        player.Pause();
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.Equal(TimeSpan.FromSeconds(1.5), player.Position);

        player.Play();
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(2.5), player.Position);
        Assert.Equal([PlaybackState.Paused, PlaybackState.Playing, PlaybackState.Paused, PlaybackState.Playing], _events);
    }

    [Fact]
    public async Task RepeatedCalls_AreNoOpsWithoutEvents()
    {
        using var player = await OpenAsync(TimeSpan.FromSeconds(10));

        player.Pause();
        player.Play();
        _time.Advance(TimeSpan.FromSeconds(1));
        player.Play();

        Assert.Equal(TimeSpan.FromSeconds(1), player.Position);
        Assert.Equal([PlaybackState.Paused, PlaybackState.Playing], _events);
    }

    [Fact]
    public async Task ReachingTheEnd_EndsOnceAndClampsThePosition()
    {
        using var player = await OpenAsync(TimeSpan.FromSeconds(2));

        player.Play();
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackState.Ended, player.State);
        Assert.Equal(TimeSpan.FromSeconds(2), player.Position);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(2), player.Position);
        Assert.Equal([PlaybackState.Paused, PlaybackState.Playing, PlaybackState.Ended], _events);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public async Task Position_NeverExceedsDurationEvenBeforeTheEndTimerFires()
    {
        using var player = await OpenAsync(TimeSpan.FromSeconds(1));
        var lateTime = new ManualTimeProvider();
        using var late = new ClockAudioPlayer(lateTime);
        await late.OpenAsync(player.Tracks, CancellationToken.None);

        late.Play();
        Assert.Equal(TimeSpan.Zero, late.Position);
        lateTime.Advance(TimeSpan.FromSeconds(0.999));
        Assert.Equal(PlaybackState.Playing, late.State);
        Assert.True(late.Position <= late.Duration);
    }

    [Fact]
    public async Task Play_AfterEnded_RestartsFromZero()
    {
        using var player = await OpenAsync(TimeSpan.FromSeconds(2));
        player.Play();
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(PlaybackState.Ended, player.State);

        player.Play();
        _time.Advance(TimeSpan.FromSeconds(0.5));

        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(TimeSpan.FromSeconds(0.5), player.Position);
    }

    [Fact]
    public async Task Pause_AfterEnded_IsANoOp()
    {
        using var player = await OpenAsync(TimeSpan.FromSeconds(1));
        player.Play();
        _time.Advance(TimeSpan.FromSeconds(1));

        player.Pause();

        Assert.Equal(PlaybackState.Ended, player.State);
        Assert.Equal(TimeSpan.FromSeconds(1), player.Position);
    }

    [Fact]
    public async Task Seek_ClampsAndKeepsPausedOrPlaying()
    {
        using var player = await OpenAsync(TimeSpan.FromSeconds(10));

        player.Seek(TimeSpan.FromSeconds(4));
        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.Equal(TimeSpan.FromSeconds(4), player.Position);

        player.Seek(TimeSpan.FromSeconds(-3));
        Assert.Equal(TimeSpan.Zero, player.Position);

        player.Play();
        _time.Advance(TimeSpan.FromSeconds(1));
        player.Seek(TimeSpan.FromSeconds(7));
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(TimeSpan.FromSeconds(7), player.Position);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(8), player.Position);

        // The end timer follows the seek: 2 s left from 8 s.
        _time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal(PlaybackState.Playing, player.State);
        _time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(PlaybackState.Ended, player.State);
    }

    [Fact]
    public async Task Seek_PastTheEndWhilePlaying_Ends()
    {
        using var player = await OpenAsync(TimeSpan.FromSeconds(10));
        player.Play();

        player.Seek(TimeSpan.FromSeconds(60));

        Assert.Equal(PlaybackState.Ended, player.State);
        Assert.Equal(TimeSpan.FromSeconds(10), player.Position);
    }

    [Fact]
    public async Task Seek_WhileEnded_PausesAtTheTarget()
    {
        using var player = await OpenAsync(TimeSpan.FromSeconds(3));
        player.Play();
        _time.Advance(TimeSpan.FromSeconds(3));

        player.Seek(TimeSpan.FromSeconds(1));

        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.Equal(TimeSpan.FromSeconds(1), player.Position);
        Assert.Equal([PlaybackState.Paused, PlaybackState.Playing, PlaybackState.Ended, PlaybackState.Paused], _events);
    }

    [Fact]
    public async Task Seek_ToTheEndWhilePaused_ThenPlay_Ends()
    {
        using var player = await OpenAsync(TimeSpan.FromSeconds(3));
        player.Seek(TimeSpan.FromSeconds(3));
        Assert.Equal(PlaybackState.Paused, player.State);

        player.Play();

        Assert.Equal(PlaybackState.Ended, player.State);
    }

    [Fact]
    public async Task ZeroLengthSession_PlayEndsImmediately()
    {
        using var player = await OpenAsync(TimeSpan.Zero);

        player.Play();

        Assert.Equal(PlaybackState.Ended, player.State);
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    [Fact]
    public async Task Reopen_ResetsToPausedAtZero()
    {
        using var player = await OpenAsync(TimeSpan.FromSeconds(5));
        player.Play();
        _time.Advance(TimeSpan.FromSeconds(2));

        await player.OpenAsync([TestWav.Silence(_dir.File("other.wav"), TimeSpan.FromSeconds(1))], CancellationToken.None);

        Assert.Equal(PlaybackState.Paused, player.State);
        Assert.Equal(TimeSpan.Zero, player.Position);
        Assert.Equal(TimeSpan.FromSeconds(1), player.Duration);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(PlaybackState.Paused, player.State); // the old end timer is gone
    }

    [Fact]
    public async Task Dispose_ClosesAndRejectsFurtherCalls()
    {
        var player = await OpenAsync(TimeSpan.FromSeconds(5));
        player.Play();

        player.Dispose();
        player.Dispose();

        Assert.Equal(PlaybackState.Closed, player.State);
        Assert.Equal(PlaybackState.Closed, _events[^1]);
        Assert.Throws<ObjectDisposedException>(player.Play);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => player.OpenAsync(player.Tracks.Count == 0 ? [TestWav.Silence(_dir.File("x.wav"), TimeSpan.FromSeconds(1))] : player.Tracks, CancellationToken.None).AsTask());
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public async Task RealClock_EndsOnATimerThread()
    {
        var wav = TestWav.Silence(_dir.File("short.wav"), TimeSpan.FromMilliseconds(100));
        using var player = new ClockAudioPlayback().CreatePlayer();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.StateChanged += s =>
        {
            if (s == PlaybackState.Ended)
            {
                ended.TrySetResult();
            }
        };
        await player.OpenAsync([wav], CancellationToken.None);

        player.Play();

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(TimeSpan.FromMilliseconds(100), player.Position);
    }

    private async Task<ClockAudioPlayer> OpenAsync(params TimeSpan[] durations)
    {
        var paths = new List<string>();
        for (var i = 0; i < durations.Length; i++)
        {
            paths.Add(TestWav.Silence(_dir.File($"track{i}.wav"), durations[i]));
        }

        var player = new ClockAudioPlayer(_time);
        player.StateChanged += s =>
        {
            lock (_events)
            {
                _events.Add(s);
            }
        };
        await player.OpenAsync(paths, CancellationToken.None);
        return player;
    }
}
