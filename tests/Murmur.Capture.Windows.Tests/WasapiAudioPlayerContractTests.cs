using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Playback;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// The player state machine (the ClockAudioPlayer contract) driven through a scripted engine, so it runs on any OS.
/// </summary>
public sealed class WasapiAudioPlayerContractTests : IDisposable
{
    private const int Rate = 16_000;

    private readonly TempDir _dir = new();
    private readonly FakeEngineFactory _engines = new();
    private readonly WasapiAudioPlayer _player;
    private readonly List<PlaybackState> _changes = [];
    private readonly List<CaptureFault> _faults = [];
    private readonly string _mic;
    private readonly string _system;

    public WasapiAudioPlayerContractTests()
    {
        _mic = _dir.Write("mic.wav", TestWav.Pcm16(TestWav.Constant(3 * Rate, 100)));        // 3 s
        _system = _dir.Write("system.wav", TestWav.Pcm16(TestWav.Constant(2 * Rate, 200))); // 2 s
        _player = new WasapiAudioPlayer(_engines.Create);
        _player.StateChanged += _changes.Add;
        _player.Faulted += _faults.Add;
    }

    public void Dispose()
    {
        _player.Dispose();
        _dir.Dispose();
    }

    private FakeEngine Engine => _engines.Last;

    private void Open() => OpenAsync(_mic, _system).AsTask().GetAwaiter().GetResult();

    private ValueTask OpenAsync(params string[] paths) => _player.OpenAsync(paths, TestContext.Current.CancellationToken);

    [Fact]
    public void MutedTracks_ReachTheMixer_AlsoOfAnEngineCreatedLater()
    {
        Assert.True(_player.CanMuteTracks);
        Assert.False(_player.CanChangeSpeed);
        _player.SetTrackMuted(1, true);
        Open();
        Assert.Equal(0b10, Engine.Mixer.MutedMask);

        _player.SetTrackMuted(0, true);
        _player.SetTrackMuted(1, false);
        Assert.Equal(0b01, Engine.Mixer.MutedMask);

        Open();
        Assert.Equal(0b01, Engine.Mixer.MutedMask);
        _player.Speed = 2;
        Assert.Equal(1, _player.Speed);
    }

    [Fact]
    public void NewPlayer_IsClosed_AndRejectsCommands()
    {
        Assert.Equal(PlaybackState.Closed, _player.State);
        Assert.Equal(TimeSpan.Zero, _player.Duration);
        Assert.Equal(TimeSpan.Zero, _player.Position);
        Assert.Throws<InvalidOperationException>(_player.Play);
        Assert.Throws<InvalidOperationException>(_player.Pause);
        Assert.Throws<InvalidOperationException>(() => _player.Seek(TimeSpan.Zero));
    }

    [Fact]
    public void Open_LeavesPausedAtZero_WithTheLongestDuration()
    {
        Open();

        Assert.Equal(PlaybackState.Paused, _player.State);
        Assert.Equal(TimeSpan.FromSeconds(3), _player.Duration);
        Assert.Equal(TimeSpan.Zero, _player.Position);
        Assert.Equal([PlaybackState.Paused], _changes);
        Assert.Equal(0, Engine.StartFrame);
    }

    [Fact]
    public async Task Open_Failures_LeaveThePlayerUnchanged()
    {
        Open();
        var bad = _dir.Write("stereo.wav", TestWav.Raw(1, 2, Rate, 16, 10));
        var otherRate = _dir.Write("48k.wav", TestWav.Pcm16(TestWav.Ramp(10), 48_000));

        await Assert.ThrowsAsync<FileNotFoundException>(async () => await OpenAsync(_mic, Path.Combine(_dir.Path, "nope.wav")));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await OpenAsync(bad));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await OpenAsync(_mic, otherRate));
        await Assert.ThrowsAsync<ArgumentException>(async () => await OpenAsync());
        _engines.FailWith = new InvalidOperationException("No default output (render endpoint) is available for playback.");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await OpenAsync(_mic));

        Assert.Single(_engines.Engines);
        Assert.False(Engine.Disposed);
        Assert.Equal(PlaybackState.Paused, _player.State);
        Assert.Equal(TimeSpan.FromSeconds(3), _player.Duration);
    }

    [Fact]
    public async Task Reopen_ReplacesTheTracks_AndDisposesTheOldEngine()
    {
        Open();
        _player.Play();
        var first = Engine;

        await OpenAsync(_system);

        Assert.True(first.Disposed);
        Assert.Equal(2, _engines.Engines.Count);
        Assert.Equal(PlaybackState.Paused, _player.State);
        Assert.Equal(TimeSpan.FromSeconds(2), _player.Duration);
        Assert.Equal(TimeSpan.Zero, _player.Position);
    }

    [Fact]
    public void PlayPause_FollowTheEnginePosition()
    {
        Open();

        _player.Play();
        _player.Play(); // no-op
        Assert.Equal(PlaybackState.Playing, _player.State);
        Engine.PlayedFrame = Rate + (Rate / 2);
        Assert.Equal(TimeSpan.FromSeconds(1.5), _player.Position);

        _player.Pause();
        Engine.PlayedFrame = 2 * Rate; // a paused position must not move with the engine
        Assert.Equal(PlaybackState.Paused, _player.State);
        Assert.Equal(TimeSpan.FromSeconds(1.5), _player.Position);
        _player.Pause(); // no-op

        Assert.Equal(["play", "pause"], Engine.Commands);
        Assert.Equal([PlaybackState.Paused, PlaybackState.Playing, PlaybackState.Paused], _changes);
    }

    [Fact]
    public void Position_NeverExceedsDuration()
    {
        Open();
        _player.Play();
        Engine.PlayedFrame = 10 * Rate;
        Assert.Equal(TimeSpan.FromSeconds(3), _player.Position);
    }

    [Fact]
    public void Seek_ClampsAndFlushes_KeepingPlayOrPause()
    {
        Open();

        _player.Seek(TimeSpan.FromSeconds(1));
        Assert.Equal(PlaybackState.Paused, _player.State);
        Assert.Equal(TimeSpan.FromSeconds(1), _player.Position);

        _player.Seek(TimeSpan.FromSeconds(-5));
        Assert.Equal(TimeSpan.Zero, _player.Position);

        _player.Play();
        _player.Seek(TimeSpan.FromSeconds(2.5));
        Assert.Equal(PlaybackState.Playing, _player.State);
        Assert.Equal(TimeSpan.FromSeconds(2.5), _player.Position);

        Assert.Equal(["seek:16000", "seek:0", "play", "seek:40000:play"], Engine.Commands);
    }

    [Fact]
    public void SeekToTheEnd_WhilePlaying_Ends_AndBeforeTheEnd_WhileEnded_Pauses()
    {
        Open();
        _player.Play();

        _player.Seek(TimeSpan.FromSeconds(99));
        Assert.Equal(PlaybackState.Ended, _player.State);
        Assert.Equal(TimeSpan.FromSeconds(3), _player.Position);

        _player.Seek(TimeSpan.FromSeconds(10)); // still the end: stays Ended
        Assert.Equal(PlaybackState.Ended, _player.State);

        _player.Seek(TimeSpan.FromSeconds(1));
        Assert.Equal(PlaybackState.Paused, _player.State);
        Assert.Equal(TimeSpan.FromSeconds(1), _player.Position);
        Assert.Equal(
            [PlaybackState.Paused, PlaybackState.Playing, PlaybackState.Ended, PlaybackState.Paused],
            _changes);
    }

    [Fact]
    public void Ended_IsRaisedOnce_AndPlayRestartsFromZero()
    {
        Open();
        _player.Play();

        Engine.FireEnded();
        Engine.FireEnded(); // a duplicate notification changes nothing

        Assert.Equal(PlaybackState.Ended, _player.State);
        Assert.Equal(TimeSpan.FromSeconds(3), _player.Position);
        Assert.Single(_changes, s => s == PlaybackState.Ended);

        _player.Pause(); // no-op in Ended
        Assert.Equal(PlaybackState.Ended, _player.State);

        _player.Play();
        Assert.Equal(PlaybackState.Playing, _player.State);
        Assert.Equal(TimeSpan.Zero, _player.Position);
        Assert.Equal("seek:0:play", Engine.Commands[^1]);
    }

    [Fact]
    public void StaleEnd_AfterASeek_IsIgnored()
    {
        Open();
        _player.Play();
        var generation = Engine.Generation;
        _player.Seek(TimeSpan.FromSeconds(1)); // supersedes the end the render thread was about to report

        ((IPlaybackEngineOwner)_player).OnEnded(Engine, generation);

        Assert.Equal(PlaybackState.Playing, _player.State);
    }

    [Fact]
    public void PlayAtTheVeryEnd_GoesStraightToEnded()
    {
        Open();
        _player.Seek(TimeSpan.FromSeconds(3));
        Assert.Equal(PlaybackState.Paused, _player.State);

        _player.Play();

        Assert.Equal(PlaybackState.Ended, _player.State);
        Assert.DoesNotContain("play", Engine.Commands);
    }

    [Fact]
    public void DeviceInvalidated_WhilePlaying_PausesAtTheHeardPosition_AndSurfacesTheFault()
    {
        Open();
        _player.Play();
        Engine.PlayedFrame = Rate;
        var dead = Engine;

        dead.FireFault();

        Assert.True(dead.Disposed);
        Assert.Equal(PlaybackState.Paused, _player.State);
        Assert.Equal(TimeSpan.FromSeconds(1), _player.Position);
        Assert.Equal(PlaybackState.Paused, _changes[^1]);
        var fault = Assert.Single(_faults);
        Assert.Contains("DEVICE_INVALIDATED", fault.Message, StringComparison.Ordinal);
        Assert.Same(fault, _player.LastFault);

        // Play reopens the default endpoint at the held position.
        _player.Play();
        Assert.Equal(2, _engines.Engines.Count);
        Assert.Equal(Rate, Engine.StartFrame);
        Assert.Equal(["play"], Engine.Commands);
        Assert.Equal(PlaybackState.Playing, _player.State);
    }

    [Fact]
    public void FailedCommand_IsAFault_NotAnException()
    {
        Open();
        _player.Play();
        Engine.PlayedFrame = Rate / 2;
        Engine.FailNext = WasapiConstants.AudclntEDeviceInvalidated;

        _player.Pause();

        Assert.Equal(PlaybackState.Paused, _player.State);
        Assert.Equal(TimeSpan.FromSeconds(0.5), _player.Position);
        Assert.Single(_faults);
        Assert.True(Engine.Disposed);
    }

    [Fact]
    public void Play_WhenNoEndpointCanBeReopened_StaysPaused_AndReportsAFault()
    {
        Open();
        _player.Play();
        Engine.FireFault();
        _engines.FailWith = new InvalidOperationException("No default output (render endpoint) is available for playback.");

        _player.Play();

        Assert.Equal(PlaybackState.Paused, _player.State);
        Assert.Equal(2, _faults.Count);
        Assert.Contains("No default output", _faults[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceInvalidated_WhilePaused_ASinglePlayReopensTheEndpoint_AtTheHeldPosition()
    {
        Open();
        _player.Play();
        Engine.PlayedFrame = Rate;
        _player.Pause();
        var dead = Engine;
        dead.FailNext = WasapiConstants.AudclntEDeviceInvalidated; // died unnoticed: the render loop was idle

        _player.Play();

        Assert.True(dead.Disposed);
        Assert.Equal(2, _engines.Engines.Count);
        Assert.Equal(Rate, Engine.StartFrame);
        Assert.Equal(["play"], Engine.Commands);
        Assert.Equal(PlaybackState.Playing, _player.State);
        Assert.Equal(TimeSpan.FromSeconds(1), _player.Position);
        Assert.Equal([PlaybackState.Paused, PlaybackState.Playing, PlaybackState.Paused, PlaybackState.Playing], _changes);
        Assert.Empty(_faults);
    }

    [Fact]
    public void DeviceInvalidated_WhileEnded_ASinglePlayRestartsFromZero_OnANewEndpoint()
    {
        Open();
        _player.Play();
        Engine.FireEnded();
        var dead = Engine;
        dead.FailNext = WasapiConstants.AudclntEDeviceInvalidated;

        _player.Play();

        Assert.True(dead.Disposed);
        Assert.Equal(2, _engines.Engines.Count);
        Assert.Equal(0, Engine.StartFrame);
        Assert.Equal(["play"], Engine.Commands);
        Assert.Equal(PlaybackState.Playing, _player.State);
        Assert.Equal(TimeSpan.Zero, _player.Position);
        Assert.Equal(PlaybackState.Playing, _changes[^1]);
        Assert.Empty(_faults);
    }

    [Fact]
    public void DeviceInvalidated_WhileEnded_AndNoEndpoint_StaysEndedAtTheEnd_AndReportsAFault()
    {
        Open();
        _player.Play();
        Engine.FireEnded();
        Engine.FailNext = WasapiConstants.AudclntEDeviceInvalidated;
        _engines.FailWith = new InvalidOperationException("No default output (render endpoint) is available for playback.");
        var changes = _changes.Count;

        _player.Play();

        Assert.Equal(PlaybackState.Ended, _player.State);
        Assert.Equal(_player.Duration, _player.Position);
        Assert.Equal(changes, _changes.Count);
        var fault = Assert.Single(_faults);
        Assert.Contains("No default output", fault.Message, StringComparison.Ordinal);
        Assert.Same(fault, _player.LastFault);

        // Once an endpoint is back, the next Play restarts from 0.
        _engines.FailWith = null;
        _player.Play();
        Assert.Equal(PlaybackState.Playing, _player.State);
        Assert.Equal(0, Engine.StartFrame);
    }

    [Fact]
    public void DeviceInvalidated_WhilePaused_AndTheRetryFailsToo_StaysPausedAtTheHeldPosition()
    {
        Open();
        _player.Seek(TimeSpan.FromSeconds(2));
        Engine.FailNext = WasapiConstants.AudclntEDeviceInvalidated;
        _engines.NewEngineFailNext = WasapiConstants.AudclntEDeviceInvalidated;
        var changes = _changes.Count;

        _player.Play();

        Assert.Equal(2, _engines.Engines.Count);
        Assert.All(_engines.Engines, e => Assert.True(e.Disposed));
        Assert.Equal(PlaybackState.Paused, _player.State);
        Assert.Equal(TimeSpan.FromSeconds(2), _player.Position);
        Assert.Equal(changes, _changes.Count);
        var fault = Assert.Single(_faults);
        Assert.Contains("DEVICE_INVALIDATED", fault.Message, StringComparison.Ordinal);

        // The retry happens once per Play: the next Play opens a third engine.
        _engines.NewEngineFailNext = 0;
        _player.Play();
        Assert.Equal(3, _engines.Engines.Count);
        Assert.Equal(2 * Rate, Engine.StartFrame);
        Assert.Equal(PlaybackState.Playing, _player.State);
    }

    [Fact]
    public void SeekWhilePausedWithoutAnEngine_OnlyMovesThePosition()
    {
        Open();
        _player.Play();
        Engine.FireFault();

        _player.Seek(TimeSpan.FromSeconds(2));
        _player.Play();

        Assert.Equal(2 * Rate, Engine.StartFrame);
    }

    [Fact]
    public async Task Dispose_ClosesAndReleases_ThenEverythingThrows()
    {
        Open();
        _player.Play();
        var engine = Engine;

        _player.Dispose();
        _player.Dispose();

        Assert.True(engine.Disposed);
        Assert.Equal(PlaybackState.Closed, _player.State);
        Assert.Equal(PlaybackState.Closed, _changes[^1]);
        Assert.Throws<ObjectDisposedException>(_player.Play);
        Assert.Throws<ObjectDisposedException>(_player.Pause);
        Assert.Throws<ObjectDisposedException>(() => _player.Seek(TimeSpan.Zero));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await OpenAsync(_mic));

        engine.FireEnded(); // a late notification is ignored
        Assert.Equal(PlaybackState.Closed, _player.State);
    }

    [Fact]
    public void Playback_FactoriesHandOutClosedPlayers()
    {
        using var fromPlayback = new WasapiAudioPlayback().CreatePlayer();
        using var fromCapture = new WindowsCapture().CreatePlayer();

        Assert.IsType<WasapiAudioPlayer>(fromPlayback);
        Assert.Equal(PlaybackState.Closed, fromPlayback.State);
        Assert.Equal(PlaybackState.Closed, fromCapture.State);
    }
}
