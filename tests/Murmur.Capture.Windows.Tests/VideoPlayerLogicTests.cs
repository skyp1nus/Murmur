using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Video;
using Murmur.Capture.Windows.Video.Interop;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Tests;

/// <summary>The platform-free parts of the Media Engine player: when a frame is due, the row copy, errors, output choice.</summary>
public sealed class VideoPlayerLogicTests
{
    [Fact]
    public void FrameTracker_NothingPresented_NothingDue()
    {
        var tracker = new FrameChangeTracker();
        Assert.False(tracker.IsDue(320, 240, 0));
        tracker.Tick(VideoConstants.SFalse, 0); // S_FALSE: no new frame
        Assert.False(tracker.IsDue(320, 240, 0));
        tracker.Tick(0, -1); // S_OK without a time stamp
        Assert.False(tracker.IsDue(320, 240, 0));
        Assert.Null(tracker.CopiedPts);
    }

    [Fact]
    public void FrameTracker_NewTimeStamp_IsDueOnce()
    {
        var tracker = new FrameChangeTracker();
        tracker.Tick(0, 0);
        Assert.True(tracker.IsDue(320, 240, 0));
        tracker.Copied(320, 240, 0);
        Assert.Equal(0, tracker.CopiedPts);
        Assert.False(tracker.IsDue(320, 240, 0));

        tracker.Tick(VideoConstants.SFalse, 0);
        Assert.False(tracker.IsDue(320, 240, 0));

        tracker.Tick(0, 333_333);
        Assert.True(tracker.IsDue(320, 240, 0));
        tracker.Copied(320, 240, 0);
        Assert.Equal(333_333, tracker.CopiedPts);
        tracker.Tick(0, 333_333); // the same frame reported again
        Assert.False(tracker.IsDue(320, 240, 0));
    }

    [Fact]
    public void FrameTracker_AnotherSize_IsDue_WithoutANewFrame()
    {
        var tracker = new FrameChangeTracker();
        tracker.Tick(0, 0);
        tracker.Copied(320, 240, 0);
        Assert.True(tracker.IsDue(640, 360, 0));
        Assert.True(tracker.IsDue(320, 241, 0));
        tracker.Copied(640, 360, 0);
        Assert.False(tracker.IsDue(640, 360, 0));
    }

    [Fact]
    public void FrameTracker_PictureEvent_IsDue_EvenWithoutATimeStamp()
    {
        // A seek while paused: the engine reports SEEKED; OnVideoStreamTick may not report the new frame.
        var tracker = new FrameChangeTracker();
        Assert.True(tracker.IsDue(320, 240, 1)); // first frame ready
        tracker.Copied(320, 240, 1);
        Assert.False(tracker.IsDue(320, 240, 1));
        Assert.True(tracker.IsDue(320, 240, 2)); // seek completed
        tracker.Copied(320, 240, 2);
        Assert.False(tracker.IsDue(320, 240, 2));
    }

    [Fact]
    public void FrameTracker_Reset_ForgetsTheLastCopy()
    {
        var tracker = new FrameChangeTracker();
        tracker.Tick(0, 10);
        tracker.Copied(320, 240, 3);
        tracker.Reset();
        Assert.False(tracker.IsDue(320, 240, 0));
        Assert.Null(tracker.CopiedPts);
    }

    [Theory]
    [InlineData(0, 10, 40)]
    [InlineData(10, 0, 40)]
    [InlineData(-1, 10, 40)]
    [InlineData(10, 10, 39)] // stride below width * 4
    [InlineData(20_000, 10, 80_000)]
    public void Validate_RejectsBadSizes(int width, int height, int stride)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BgraFrame.Validate(1 << 20, width, height, stride));
    }

    [Fact]
    public void Validate_RejectsATooSmallBuffer_AndAcceptsAnExactOne()
    {
        // The last row needs only width * 4 bytes, not a whole stride.
        BgraFrame.Validate((64 * 9) + 40, 10, 10, 64);
        Assert.Throws<ArgumentException>(() => BgraFrame.Validate((64 * 9) + 39, 10, 10, 64));
    }

    [Theory]
    [InlineData(3, 2, 12, 12)] // tight on both sides: one block copy
    [InlineData(3, 2, 16, 12)] // padded source rows
    [InlineData(3, 2, 12, 20)] // padded destination rows
    [InlineData(5, 4, 32, 24)]
    public unsafe void CopyRows_CopiesEveryRow_AndLeavesThePaddingAlone(int width, int height, int pitch, int stride)
    {
        var source = new byte[pitch * height];
        for (var i = 0; i < source.Length; i++)
        {
            source[i] = (byte)(i + 1);
        }

        var destination = new byte[stride * height];
        destination.AsSpan().Fill(0xEE);
        fixed (byte* p = source)
        {
            BgraFrame.CopyRows(p, pitch, destination, width, height, stride);
        }

        for (var y = 0; y < height; y++)
        {
            Assert.Equal(source.AsSpan(y * pitch, width * 4).ToArray(), destination.AsSpan(y * stride, width * 4).ToArray());
            Assert.All(destination.AsSpan((y * stride) + (width * 4), stride - (width * 4)).ToArray(), b => Assert.Equal(0xEE, b));
        }
    }

    [Fact]
    public void Errors_DecodeAndUnsupportedSource_AreInvalidData()
    {
        var decode = MediaEngineErrors.OpenFailed(@"C:\s\screen-001.mp4", VideoConstants.ErrDecode, unchecked((int)0xC00D36B4));
        Assert.IsType<InvalidDataException>(decode);
        Assert.Contains("screen-001.mp4", decode.Message, StringComparison.Ordinal);
        Assert.Contains("MF_MEDIA_ENGINE_ERR_DECODE", decode.Message, StringComparison.Ordinal);
        Assert.Contains("0xC00D36B4", decode.Message, StringComparison.Ordinal);
        Assert.Equal(unchecked((int)0xC00D36B4), Assert.IsType<COMException>(decode.InnerException).HResult);

        Assert.IsType<InvalidDataException>(MediaEngineErrors.OpenFailed("x.mp4", VideoConstants.ErrSrcNotSupported, 0));
        Assert.IsType<InvalidDataException>(MediaEngineErrors.OpenFailed("x.mp4", VideoConstants.ErrNetwork, VideoConstants.MfEUnsupportedByteStreamType));
        Assert.IsType<IOException>(MediaEngineErrors.OpenFailed("x.mp4", VideoConstants.ErrNetwork, unchecked((int)0x80070005)));
        Assert.Equal("media engine error 42", MediaEngineErrors.Name(42));
        Assert.Equal("MF_MEDIA_ENGINE_ERR_ABORTED", MediaEngineErrors.Describe(VideoConstants.ErrAborted, 0));
    }

    [Theory]
    [InlineData("Auto", true, false, true, "Direct3D")]  // a real GPU with video support
    [InlineData("Auto", true, true, true, "Wic")]        // WARP / Basic Render Driver
    [InlineData("Auto", true, false, false, "Wic")]      // no video support
    [InlineData("Auto", false, true, false, "Wic")]      // no D3D11 at all
    [InlineData("Direct3D", true, true, true, "Direct3D")]
    [InlineData("Wic", true, false, true, "Wic")]
    public void OutputPolicy_UsesDirect3DOnlyOnARealGpu(string requested, bool device, bool software, bool video, string expected)
    {
        Assert.Equal(Enum.Parse<VideoOutput>(expected), VideoOutputPolicy.Choose(Enum.Parse<VideoOutput>(requested), device, software, video));
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(-1.0, 0)]
    [InlineData(0.0, 0)]
    [InlineData(1.5, 15_000_000)]
    [InlineData(3600.0, 36_000_000_000)]
    public void SecondsToTime_HandlesTheEngineSpecialValues(double seconds, long ticks)
    {
        Assert.Equal(TimeSpan.FromTicks(ticks), MediaEngineVideoPlayer.SecondsToTime(seconds));
    }

    [Fact]
    public void ClosedPlayer_ReportsNothing_AndRejectsCommands()
    {
        using var player = new MediaEngineVideoPlayer();
        Assert.False(player.IsOpen);
        Assert.False(player.IsPlaying);
        Assert.Equal(TimeSpan.Zero, player.Duration);
        Assert.Equal(TimeSpan.Zero, player.Position);
        Assert.Equal(default(VideoSize), player.Size);
        Assert.False(player.TryCopyFrame(new byte[16 * 16 * 4], 16, 16, 64));
        Assert.Throws<InvalidOperationException>(player.Play);
        Assert.Throws<InvalidOperationException>(player.Pause);
        Assert.Throws<InvalidOperationException>(() => player.Seek(TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => player.TryCopyFrame(new byte[10], 16, 16, 64));
        player.Close(); // no-op
    }

    [Fact]
    public async Task OpenAsync_MissingFile_ThrowsFileNotFound_BeforeTouchingNativeCode()
    {
        using var player = new MediaEngineVideoPlayer();
        var path = Path.Combine(Path.GetTempPath(), "murmur-no-such-video-" + Guid.NewGuid().ToString("N") + ".mp4");
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(async () => await player.OpenAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(path, ex.FileName);
        Assert.False(player.IsOpen);
    }

    [Fact]
    public async Task DisposedPlayer_Throws()
    {
        var player = new MediaEngineVideoPlayer();
        player.Dispose();
        player.Dispose(); // idempotent
        player.Close();   // no-op after dispose
        Assert.Throws<ObjectDisposedException>(() => player.TryCopyFrame(new byte[64], 4, 4, 16));
        Assert.Throws<ObjectDisposedException>(player.Play);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await player.OpenAsync("x.mp4", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void WindowsCapture_IsTheVideoPlaybackToo()
    {
        IVideoPlayback playback = new WindowsCapture();
        using var player = playback.CreateVideoPlayer();
        Assert.IsType<MediaEngineVideoPlayer>(player);
        Assert.False(player.IsOpen);
    }

    [Fact]
    public void SeekQueue_SendsTheFirstSeekAtOnce_AndQueuesTheNextUntilSeeked()
    {
        var queue = new SeekQueue(timeout: 2_000);
        Assert.False(queue.IsBusy);
        Assert.True(queue.Request(S(2.25), seekedEvents: 0, now: 0));
        Assert.True(queue.IsBusy);
        Assert.Equal(S(2.25), queue.LatestTarget);

        // A second seek before SEEKED: queued, and the position reports it already.
        Assert.False(queue.Request(S(0.75), 0, 10));
        Assert.Equal(S(0.75), queue.LatestTarget);
        Assert.False(queue.TryNext(0, 20, out _));

        // A third one replaces the queued target; only the latest is sent.
        Assert.False(queue.Request(S(3.75), 0, 30));
        Assert.Equal(S(3.75), queue.LatestTarget);

        // SEEKED of the first: the queued one goes out, and is in flight until its own SEEKED.
        Assert.True(queue.TryNext(1, 40, out var next));
        Assert.Equal(S(3.75), next);
        Assert.True(queue.IsBusy);
        Assert.False(queue.TryNext(1, 50, out _));
        Assert.False(queue.TryNext(2, 60, out _));
        Assert.False(queue.IsBusy);

        // Idle again: the next seek is sent at once.
        Assert.True(queue.Request(S(1), 2, 70));
    }

    [Fact]
    public void SeekQueue_GivesUpWaitingAfterTheTimeout()
    {
        var queue = new SeekQueue(timeout: 2_000);
        Assert.True(queue.Request(S(1), 0, 0));
        Assert.False(queue.Request(S(2), 0, 100));
        Assert.False(queue.TryNext(0, 1_999, out _));
        Assert.True(queue.TryNext(0, 2_000, out var next)); // SEEKED never came
        Assert.Equal(S(2), next);
        Assert.False(queue.Request(S(3), 0, 2_100)); // the timed-out one's successor is in flight now
        Assert.True(queue.TryNext(0, 4_000, out next));
        Assert.Equal(S(3), next);
        queue.Reset();
        Assert.False(queue.IsBusy);
    }

    private const long RecoveryDelay = 300;
    private const long NoPts = FrameChangeTracker.None;
    private const long Period = 333_333; // 30 fps

    private static SeekRecovery ArmedAndQuiet(double target, long baseline, long pts)
    {
        var recovery = new SeekRecovery(RecoveryDelay);
        recovery.Arm(S(target), baseline);
        recovery.IsDue(0, seekBusy: false, pts, Period); // the first idle check starts the wait
        return recovery;
    }

    /// <summary>The next cure: the wait starts at the next idle check and must last <see cref="RecoveryDelay"/>.</summary>
    private static SeekRecoveryStep NextStep(ref SeekRecovery recovery, ref long now)
    {
        now += 1;
        Assert.False(recovery.IsDue(now, seekBusy: false, 0, Period));
        now += RecoveryDelay;
        Assert.True(recovery.IsDue(now, false, 0, Period));
        return recovery.Take(engineSettled: true, now);
    }

    [Fact]
    public void SeekRecovery_NotArmed_NeverDue()
    {
        var recovery = new SeekRecovery(RecoveryDelay);
        Assert.False(recovery.IsArmed);
        Assert.False(recovery.IsDue(0, seekBusy: false, presentedPts: 0));
        Assert.False(recovery.IsDue(10_000, false, 0));
        Assert.Equal(SeekRecoveryStep.None, recovery.Take(true, 10_000));
    }

    [Fact]
    public void SeekRecovery_NewTimeStamp_DisarmsWithoutAStep()
    {
        var recovery = new SeekRecovery(RecoveryDelay);
        recovery.Arm(S(2.25), presentedPts: 0);
        Assert.False(recovery.IsDue(0, seekBusy: true, 0));
        Assert.False(recovery.IsDue(10, false, 22_500_000)); // the frame at the target was presented
        Assert.False(recovery.IsArmed);
        Assert.Equal(SeekRecoveryStep.None, recovery.TakeHealedBy()); // no step was needed
        Assert.False(recovery.IsDue(10_000, false, 22_500_000));
    }

    [Fact]
    public void SeekRecovery_WaitsForTheSeekToFinish_ThenForTheDelay()
    {
        var recovery = new SeekRecovery(RecoveryDelay);
        recovery.Arm(S(2.25), 0);

        // The seek is in flight (or queued): the clock does not run, however long it takes.
        Assert.False(recovery.IsDue(0, seekBusy: true, 0));
        Assert.False(recovery.IsDue(5_000, seekBusy: true, 0));

        // SEEKED seen at 5_000 (first idle call starts the wait), still the old frame afterwards.
        Assert.False(recovery.IsDue(5_000, seekBusy: false, 0));
        Assert.False(recovery.IsDue(5_299, false, 0));
        Assert.True(recovery.IsDue(5_300, false, 0));
    }

    [Fact]
    public void SeekRecovery_Ladder_GoesThroughEveryCureInOrder_ThenGivesUpAfterTheLastWait()
    {
        var recovery = ArmedAndQuiet(2.25, 0, 0);
        var now = 0L;
        var steps = new List<SeekRecoveryStep>();
        for (var i = 0; i < 40 && recovery.IsArmed; i++)
        {
            now += RecoveryDelay;
            if (recovery.IsDue(now, false, 0, Period))
            {
                steps.Add(recovery.Take(engineSettled: true, now));
            }
        }

        Assert.Equal(
            [
                SeekRecoveryStep.Retransfer, SeekRecoveryStep.Reseek, SeekRecoveryStep.ReseekNudged, SeekRecoveryStep.SeekAway,
                SeekRecoveryStep.SeekBack, SeekRecoveryStep.SeekExNormal, SeekRecoveryStep.SeekExApproximate,
                SeekRecoveryStep.FrameStep, SeekRecoveryStep.FrameStepBack, SeekRecoveryStep.PlayPause, SeekRecoveryStep.None,
            ],
            steps);
        Assert.Equal(SeekRecovery.MaxAttempts, steps.Count - 1);
        Assert.False(recovery.IsArmed);
        Assert.Equal(SeekRecoveryStep.None, recovery.TakeHealedBy()); // gave up: nothing healed
        Assert.False(recovery.IsDue(now + 10_000, false, 0)); // at most MaxAttempts per target
    }

    [Fact]
    public void SeekRecovery_TargetsOfTheSteps()
    {
        var recovery = new SeekRecovery(RecoveryDelay);
        recovery.Arm(S(2.25), 0);
        Assert.Equal(S(2.25), recovery.TargetFor(SeekRecoveryStep.Reseek));
        Assert.Equal(S(2.25), recovery.TargetFor(SeekRecoveryStep.SeekBack));
        Assert.Equal(S(2.25) - TimeSpan.FromMilliseconds(1), recovery.TargetFor(SeekRecoveryStep.ReseekNudged));
        Assert.Equal(S(2.05), recovery.TargetFor(SeekRecoveryStep.SeekAway));

        recovery.Arm(S(0.1), 0); // too close to the start: away goes forward, the nudge stays inside the file
        Assert.Equal(S(0.3), recovery.TargetFor(SeekRecoveryStep.SeekAway));
        Assert.Equal(S(0.1) - TimeSpan.FromMilliseconds(1), recovery.TargetFor(SeekRecoveryStep.ReseekNudged));
        recovery.Arm(TimeSpan.Zero, 7);
        Assert.Equal(TimeSpan.Zero, recovery.TargetFor(SeekRecoveryStep.ReseekNudged));
        recovery.Arm(TimeSpan.FromTicks(5_000), 7); // 0.5 ms
        Assert.Equal(TimeSpan.FromTicks(5_000), recovery.TargetFor(SeekRecoveryStep.ReseekNudged));
    }

    [Fact]
    public void SeekRecovery_Reseek_RestartsTheWaitWhenTheSeekIsBusyAgain()
    {
        var recovery = ArmedAndQuiet(1, 0, 0);
        Assert.True(recovery.IsDue(300, false, 0, Period));
        Assert.Equal(SeekRecoveryStep.Retransfer, recovery.Take(true, 300));
        Assert.False(recovery.IsDue(301, false, 0, Period)); // the wait starts over after a step
        Assert.True(recovery.IsDue(601, false, 0, Period));
        Assert.Equal(SeekRecoveryStep.Reseek, recovery.Take(true, 601));

        // The re-seek is in flight: no clock until its SEEKED, then a full delay again.
        Assert.False(recovery.IsDue(700, seekBusy: true, 0));
        Assert.False(recovery.IsDue(5_000, seekBusy: true, 0));
        Assert.False(recovery.IsDue(5_001, seekBusy: false, 0));
        Assert.False(recovery.IsDue(5_300, false, 0));
        Assert.True(recovery.IsDue(5_301, false, 0));
    }

    [Fact]
    public void SeekRecovery_EngineNotSettled_CostsNoAttempt()
    {
        var recovery = ArmedAndQuiet(1, 0, 0);
        Assert.True(recovery.IsDue(300, false, 0, Period));
        Assert.Equal(SeekRecoveryStep.None, recovery.Take(engineSettled: false, 300)); // playing, seeking or no data
        Assert.False(recovery.IsDue(301, false, 0, Period));
        Assert.True(recovery.IsDue(600, false, 0, Period));
        Assert.Equal(SeekRecoveryStep.Retransfer, recovery.Take(true, 600)); // still the first attempt
    }

    [Fact]
    public void SeekRecovery_NeverSettledEngine_GivesUpAfterMaxUnsettled()
    {
        var recovery = ArmedAndQuiet(1, 0, 0);
        var now = 0L;
        for (var i = 0; i < SeekRecovery.MaxUnsettled; i++)
        {
            Assert.True(recovery.IsArmed);
            now += RecoveryDelay;
            Assert.True(recovery.IsDue(now, false, 0, Period));
            Assert.Equal(SeekRecoveryStep.None, recovery.Take(engineSettled: false, now));
        }

        Assert.False(recovery.IsArmed);
        Assert.False(recovery.IsDue(now + 10_000, false, 0));
    }

    [Fact]
    public void SeekRecovery_NoPictureYet_NewTimeStampStillDisarms_AndNoneIsNotNew()
    {
        var recovery = new SeekRecovery(RecoveryDelay);
        recovery.Arm(S(1), NoPts);
        Assert.False(recovery.IsDue(0, false, NoPts)); // nothing presented is not "a new frame"
        Assert.True(recovery.IsArmed);
        Assert.False(recovery.IsDue(1, false, 0));
        Assert.False(recovery.IsArmed);
    }

    [Fact]
    public void SeekRecovery_ANewSeekRearmsWithAFreshBudget_DisarmAndResetStop()
    {
        var recovery = ArmedAndQuiet(1, 0, 0);
        Assert.True(recovery.IsDue(300, false, 0, Period));
        recovery.Take(true, 300);

        recovery.Arm(S(3), 0); // another click: new target, new budget, new wait
        Assert.Equal(S(3), recovery.Target);
        Assert.False(recovery.IsDue(301, false, 0));
        Assert.False(recovery.IsDue(600, false, 0));
        Assert.True(recovery.IsDue(601, false, 0));
        Assert.Equal(SeekRecoveryStep.Retransfer, recovery.Take(true, 601));

        recovery.Disarm(); // Play
        Assert.False(recovery.IsDue(10_000, false, 0));
        recovery.Arm(S(1), 0);
        recovery.Reset(); // another file
        Assert.False(recovery.IsArmed);
        Assert.False(recovery.IsDue(10_000, false, 0));
    }

    [Fact]
    public void SeekRecovery_TheFrameOfAnOlderTarget_DoesNotDisarm()
    {
        // Two quick clicks while paused: the first one's frame (pts 1 s) is presented while the second seek is in flight.
        var recovery = new SeekRecovery(RecoveryDelay);
        recovery.Arm(S(1), presentedPts: 0);
        recovery.Arm(S(2), presentedPts: 0);
        Assert.False(recovery.IsDue(5, seekBusy: true, presentedPts: 10_000_000, Period));
        Assert.True(recovery.IsArmed);

        // The second seek is done and still shows the first one's frame: the race, recovered after the delay.
        Assert.False(recovery.IsDue(10, seekBusy: false, 10_000_000, Period));
        Assert.False(recovery.IsDue(309, false, 10_000_000, Period));
        Assert.True(recovery.IsDue(310, false, 10_000_000, Period));
        Assert.Equal(SeekRecoveryStep.Retransfer, recovery.Take(true, 310));
    }

    [Fact]
    public void SeekRecovery_ANewFrameAfterTheQueueWentIdle_Disarms()
    {
        var recovery = new SeekRecovery(RecoveryDelay);
        recovery.Arm(S(1), 0);
        recovery.Arm(S(2), 0);
        Assert.False(recovery.IsDue(5, seekBusy: true, 10_000_000, Period));
        Assert.False(recovery.IsDue(10, seekBusy: false, 20_000_000, Period)); // the second target's frame
        Assert.False(recovery.IsArmed);
    }

    [Fact]
    public void SeekRecovery_TheTargetsFrame_IsSuccess_WheneverItArrived()
    {
        // The frame at the target (pts == target, or up to one period before it) arrived while the queue was still busy:
        // no step, however long the queue stays busy or idle afterwards (a step would make TryCopyFrame report a copy).
        var recovery = new SeekRecovery(RecoveryDelay);
        recovery.Arm(S(1.25), presentedPts: 37_500_000);
        Assert.False(recovery.IsDue(5, seekBusy: true, presentedPts: 12_500_000, Period));
        Assert.False(recovery.IsArmed);
        Assert.False(recovery.IsDue(10_000, seekBusy: false, 12_500_000, Period));

        recovery.Arm(S(1.25), presentedPts: 37_500_000);
        Assert.False(recovery.IsDue(5, seekBusy: false, presentedPts: 12_500_000 - Period + 1, Period)); // 1.25 s is inside the frame
        Assert.False(recovery.IsArmed);

        // A frame one period or more before the target, or after it, is not the target's.
        recovery.Arm(S(1.25), presentedPts: 37_500_000);
        Assert.False(recovery.IsDue(5, seekBusy: true, 12_500_000 - Period, Period));
        Assert.True(recovery.IsArmed);
        Assert.False(recovery.IsDue(6, seekBusy: true, 12_500_001, Period));
        Assert.True(recovery.IsArmed);

        // Frame period unknown: the default window applies (pts == target is covered).
        recovery.Arm(S(0), presentedPts: NoPts);
        Assert.False(recovery.IsDue(0, seekBusy: false, presentedPts: 0));
        Assert.False(recovery.IsArmed);
    }

    [Fact]
    public void SeekRecovery_TheStaleFrameThatIsTheTargets_NeedsNoStep()
    {
        // A seek within the frame that is shown (the baseline frame covers the target).
        var recovery = new SeekRecovery(RecoveryDelay);
        recovery.Arm(S(2.25), presentedPts: 22_500_000 - 100_000);
        Assert.False(recovery.IsDue(0, false, 22_500_000 - 100_000, Period));
        Assert.False(recovery.IsArmed);
        Assert.Equal(SeekRecoveryStep.None, recovery.TakeHealedBy());
    }

    [Fact]
    public void SeekRecovery_HealedBy_NamesTheStepBeforeTheSuccess()
    {
        var recovery = ArmedAndQuiet(2.25, 0, 0);
        var now = 0L;
        Assert.Equal(SeekRecoveryStep.Retransfer, NextStep(ref recovery, ref now));
        Assert.Equal(SeekRecoveryStep.Reseek, NextStep(ref recovery, ref now));
        Assert.False(recovery.IsDue(now + 10, seekBusy: true, 0, Period));
        Assert.False(recovery.IsDue(now + 15, seekBusy: false, 22_500_000, Period)); // the target's frame appeared after the re-seek
        Assert.False(recovery.IsArmed);
        Assert.Equal(SeekRecoveryStep.Reseek, recovery.TakeHealedBy());
        Assert.Equal(SeekRecoveryStep.None, recovery.TakeHealedBy()); // reported once
    }

    [Fact]
    public void SeekRecovery_ADetourFrame_DoesNotCountAsSuccess_TheWayBackDoes()
    {
        // SeekAway shows another frame (a new time stamp) which must not end the recovery; the seek back must.
        var recovery = ArmedAndQuiet(2.25, 0, 0);
        var now = 0L;
        while (NextStep(ref recovery, ref now) != SeekRecoveryStep.SeekAway)
        {
        }

        Assert.False(recovery.IsDue(now + 1, seekBusy: true, 20_500_000, Period));
        Assert.False(recovery.IsDue(now + 2, seekBusy: false, 20_500_000, Period)); // the detour frame, 2.05 s
        Assert.True(recovery.IsArmed);
        Assert.True(recovery.IsDue(now + 2 + RecoveryDelay, false, 20_500_000, Period));
        Assert.Equal(SeekRecoveryStep.SeekBack, recovery.Take(true, now + 2 + RecoveryDelay));
        Assert.False(recovery.IsDue(now + 2 + RecoveryDelay + 1, seekBusy: false, 22_500_000, Period)); // the target's frame
        Assert.Equal(SeekRecoveryStep.SeekBack, recovery.TakeHealedBy());
    }

    [Fact]
    public void FrameTracker_PresentedPts_FollowsTicksOnly()
    {
        var tracker = new FrameChangeTracker();
        Assert.Equal(FrameChangeTracker.None, tracker.PresentedPts);
        tracker.Tick(VideoConstants.SFalse, 777);
        Assert.Equal(FrameChangeTracker.None, tracker.PresentedPts);
        tracker.Tick(0, 333_333);
        Assert.Equal(333_333, tracker.PresentedPts);
        tracker.Reset();
        Assert.Equal(FrameChangeTracker.None, tracker.PresentedPts);
    }

    [Fact]
    public void FrameTracker_FramePeriod_IsTheShortestStepBetweenFramesSeenWhilePlaying()
    {
        var tracker = new FrameChangeTracker();
        Assert.Equal(0, tracker.FramePeriod);
        tracker.Tick(0, 0, observePeriod: true); // the first frame: nothing to compare with
        Assert.Equal(0, tracker.FramePeriod);
        tracker.Tick(0, 666_666, observePeriod: true); // a dropped frame: two periods
        Assert.Equal(666_666, tracker.FramePeriod);
        tracker.Tick(0, 1_000_000, observePeriod: true);
        Assert.Equal(333_334, tracker.FramePeriod);
        tracker.Tick(0, 50_000_000, observePeriod: false); // a seek: not observed
        tracker.Tick(0, 50_100_000, observePeriod: false);
        tracker.Tick(0, 90_000_000, observePeriod: true); // a jump of more than a second: not a period
        Assert.Equal(333_334, tracker.FramePeriod);
        tracker.Reset();
        Assert.Equal(0, tracker.FramePeriod);
    }

    [Fact]
    public void GpuCapability_OnlyARealGpuWithVideoSupport_IsHardwareVideo()
    {
        Assert.True(new GpuCapability(true, false, true, "gpu").IsHardwareVideo);
        Assert.False(new GpuCapability(true, true, true, "warp").IsHardwareVideo);
        Assert.False(new GpuCapability(true, false, false, "no video").IsHardwareVideo);
        Assert.False(new GpuCapability(false, true, false, "none").IsHardwareVideo);
    }

    [Fact]
    public void EventNames_CoverTheEngineEvents()
    {
        Assert.Equal("LOADEDMETADATA", MediaEngineEvents.Name(VideoConstants.EventLoadedMetadata));
        Assert.Equal("SEEKED", MediaEngineEvents.Name(VideoConstants.EventSeeked));
        Assert.Equal("FIRSTFRAMEREADY", MediaEngineEvents.Name(VideoConstants.EventFirstFrameReady));
        Assert.Equal("CANPLAY", MediaEngineEvents.Name(14));
        Assert.Equal("4242", MediaEngineEvents.Name(4242));
    }

    [Fact]
    public void FrameTracker_HasCopy_AfterTheFirstCopyOnly()
    {
        var tracker = new FrameChangeTracker();
        Assert.False(tracker.HasCopy);
        tracker.Copied(1, 1, 1);
        Assert.True(tracker.HasCopy);
        tracker.Reset();
        Assert.False(tracker.HasCopy);
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);
}
