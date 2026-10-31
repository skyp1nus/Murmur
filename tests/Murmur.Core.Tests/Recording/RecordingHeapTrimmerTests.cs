using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Recording;

public sealed class RecordingHeapTrimmerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const long Minimum = 1000;

    private readonly FakeRecorder _recorder = new();
    private readonly ManualTimeProvider _time = new();
    private long _allocated;
    private int _collections;

    private RecordingHeapTrimmer Create(Action? collect = null) =>
        new(_recorder, _time, Interval, Minimum, () => _allocated, collect ?? (() => _collections++));

    private static SessionRecord Session() =>
        new() { Id = SessionId.New(), Title = "t", StartedAt = DateTimeOffset.UnixEpoch, State = SessionState.Recording };

    [Fact]
    public void WhileIdle_NothingIsArmedAndNothingIsCollected()
    {
        using var trimmer = Create();

        _allocated = 10 * Minimum;
        _time.Advance(Interval * 5);

        Assert.False(trimmer.IsArmed);
        Assert.Equal(0, _time.ActiveTimers);
        Assert.Equal(0, _collections);
    }

    [Fact]
    public void WhileRecording_CollectsOncePerIntervalWhenEnoughWasAllocated()
    {
        using var trimmer = Create();
        _recorder.BeginRecording(Session());
        Assert.True(trimmer.IsArmed);

        _time.Advance(Interval - TimeSpan.FromSeconds(1));
        _allocated += Minimum;
        Assert.Equal(0, _collections);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _collections);

        _allocated += Minimum;
        _time.Advance(Interval);
        _allocated += Minimum;
        _time.Advance(Interval);
        Assert.Equal(3, _collections);
        Assert.Equal(3, trimmer.Collections);
    }

    [Fact]
    public void WhileRecording_SkipsTheCollectionWhenLittleWasAllocated()
    {
        using var trimmer = Create();
        _allocated = 5 * Minimum; // before the recording: does not count
        _recorder.BeginRecording(Session());

        _allocated += Minimum - 1;
        _time.Advance(Interval);
        Assert.Equal(0, _collections);

        // The allocation keeps adding up across skipped checks.
        _allocated += 1;
        _time.Advance(Interval);
        Assert.Equal(1, _collections);

        _time.Advance(Interval);
        Assert.Equal(1, _collections);
    }

    [Fact]
    public void WhilePaused_TheScreenStillAllocates_SoItIsTrimmedLikeARecording()
    {
        using var trimmer = Create();
        _recorder.BeginRecording(Session());
        _recorder.Pause();

        Assert.True(trimmer.IsArmed);
        for (var i = 1; i <= 4; i++)
        {
            _allocated += Minimum;
            _time.Advance(Interval);
            Assert.Equal(i, _collections);
        }

        // Resuming neither re-arms a second timer nor skips a check.
        _recorder.Resume();
        Assert.Equal(1, _time.ActiveTimers);
        _allocated += Minimum;
        _time.Advance(Interval);
        Assert.Equal(5, _collections);

        _recorder.EndRecording();
        Assert.False(trimmer.IsArmed);
    }

    [Fact]
    public void AfterTheRecording_TheTimerIsReleased_AndANewRecordingArmsItAgain()
    {
        using var trimmer = Create();
        _recorder.BeginRecording(Session());
        _recorder.EndRecording();

        Assert.False(trimmer.IsArmed);
        Assert.Equal(0, _time.ActiveTimers);
        _allocated += 10 * Minimum;
        _time.Advance(Interval * 3);
        Assert.Equal(0, _collections);

        _recorder.BeginRecording(Session());
        _allocated += Minimum;
        _time.Advance(Interval);
        Assert.Equal(1, _collections);
    }

    [Fact]
    public void ARecorderThatIsAlreadyRecording_IsTrimmedFromTheStart()
    {
        _recorder.BeginRecording(Session());
        using var trimmer = Create();

        Assert.True(trimmer.IsArmed);
        _allocated += Minimum;
        _time.Advance(Interval);
        Assert.Equal(1, _collections);
    }

    [Fact]
    public void Dispose_StopsFollowingTheRecorder()
    {
        var trimmer = Create();
        _recorder.BeginRecording(Session());

        trimmer.Dispose();
        trimmer.Dispose();

        Assert.False(trimmer.IsArmed);
        Assert.Equal(0, _time.ActiveTimers);
        _recorder.EndRecording();
        _recorder.BeginRecording(Session());
        _allocated += 10 * Minimum;
        _time.Advance(Interval * 3);
        Assert.Equal(0, _collections);
    }

    [Fact]
    public void AThrowingCollection_IsSwallowed()
    {
        using var trimmer = Create(() => throw new InvalidOperationException("boom"));
        _recorder.BeginRecording(Session());

        _allocated += Minimum;
        _time.Advance(Interval);

        Assert.Equal(0, trimmer.Collections);
        Assert.True(trimmer.IsArmed);
    }

    [Fact]
    public void InvalidSettings_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordingHeapTrimmer(_recorder, _time, TimeSpan.Zero, Minimum, () => 0, () => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordingHeapTrimmer(_recorder, _time, Interval, -1, () => 0, () => { }));
        Assert.Throws<ArgumentNullException>(() => new RecordingHeapTrimmer(null!, _time));
        Assert.Throws<ArgumentNullException>(() => new RecordingHeapTrimmer(_recorder, null!));
    }

    [Fact]
    public void Defaults_TrimEveryThirtySecondsAfterOneMegabyte()
    {
        using var trimmer = new RecordingHeapTrimmer(_recorder, _time);

        Assert.Equal(TimeSpan.FromSeconds(30), trimmer.Interval);
        Assert.Equal(1024 * 1024, trimmer.MinimumAllocatedBytes);
    }

    /// <summary>
    /// Regression for the working set that grew 3-5 MB per minute while recording: on a CPU with a large L3 the gen0
    /// budget is tens of MB, so a recording's small allocation rate ran for many minutes without a single collection.
    /// The real trimmer must induce gen0 collections while recording, whatever the budget.
    /// </summary>
    [Fact]
    public void RealCollection_RunsAGen0CollectionWhileRecording()
    {
        using var trimmer = new RecordingHeapTrimmer(_recorder, _time);
        _recorder.BeginRecording(Session());
        var before = GC.CollectionCount(0);

        // Short-lived garbage, well below any gen0 budget but above the trimmer's threshold.
        for (var i = 0; i < 64; i++)
        {
            GC.KeepAlive(new byte[32 * 1024]);
        }

        _time.Advance(trimmer.Interval);

        Assert.Equal(1, trimmer.Collections);
        Assert.True(GC.CollectionCount(0) > before);
    }
}
