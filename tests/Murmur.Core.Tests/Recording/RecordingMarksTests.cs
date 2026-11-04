using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Recording;

/// <summary>The marks of a running recording: their place on the session timeline, pause, and the save when the recording stops.</summary>
public sealed class RecordingMarksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private readonly ScriptedRecorder _recorder = new();
    private readonly InMemoryBookmarkStore _store = new();

    private RecordingMarks Create() => new(_recorder, _store);

    [Fact]
    public void TryMark_WhileIdle_AddsNothing()
    {
        using var marks = Create();
        var raised = 0;
        marks.Marked += _ => raised++;

        Assert.False(marks.TryMark(out var at));

        Assert.Equal(TimeSpan.Zero, at);
        Assert.Empty(marks.Current);
        Assert.Equal(0, raised);
    }

    [Theory]
    [InlineData(RecorderState.Starting)]
    [InlineData(RecorderState.Stopping)]
    public void TryMark_WhileStartingOrStopping_AddsNothing(RecorderState state)
    {
        using var marks = Create();
        _recorder.Start();
        _recorder.Force(state);

        Assert.False(marks.TryMark(out _));

        Assert.Empty(marks.Current);
    }

    [Fact]
    public void TryMark_WhileRecording_IsAtTheRecordersElapsedTime_AndRaisesMarked()
    {
        using var marks = Create();
        var seen = new List<TimeSpan>();
        marks.Marked += seen.Add;
        _recorder.Start();

        _recorder.Elapsed = S(12.5);
        Assert.True(marks.TryMark(out var first));
        _recorder.Elapsed = S(40);
        Assert.True(marks.TryMark(out var second));

        Assert.Equal(S(12.5), first);
        Assert.Equal(S(40), second);
        Assert.Equal([S(12.5), S(40)], marks.Current);
        Assert.Equal([S(12.5), S(40)], seen);
    }

    [Fact]
    public void TheTimeline_ExcludesThePausedTime()
    {
        using var marks = Create();
        _recorder.Start();
        _recorder.Elapsed = S(10);
        marks.TryMark(out _);

        // A pause of 5 minutes: the recorder's timeline stands still, then goes on from where it stopped.
        _recorder.Pause();
        _recorder.Resume();
        _recorder.Elapsed = S(25);
        marks.TryMark(out var after);

        Assert.Equal(S(25), after);
        Assert.Equal([S(10), S(25)], marks.Current);
    }

    [Fact]
    public void AMarkMadeWhilePaused_GetsThePositionOfThePause_AndSurvivesTheResume()
    {
        using var marks = Create();
        _recorder.Start();
        _recorder.Elapsed = S(30);
        _recorder.Pause();

        Assert.True(marks.TryMark(out var at));
        Assert.True(marks.TryMark(out var again));
        _recorder.Resume();
        _recorder.Elapsed = S(33);
        marks.TryMark(out var later);

        Assert.Equal(S(30), at);
        Assert.Equal(S(30), again);
        Assert.Equal([S(30), S(30), S(33)], marks.Current);
        Assert.Equal(S(33), later);
    }

    [Fact]
    public async Task WhenTheRecordingStops_TheMarksAreSavedForItsSession_AndCleared()
    {
        using var marks = Create();
        var session = _recorder.Start();
        _recorder.Elapsed = S(3);
        marks.TryMark(out _);
        _recorder.Pause();
        marks.TryMark(out _);
        _recorder.Resume();
        _recorder.Elapsed = S(9.25);
        marks.TryMark(out _);

        _recorder.Stop();
        await marks.PendingFlush;

        var saved = await _store.GetBookmarksAsync(session.Id, Ct);
        Assert.Equal([S(3), S(3), S(9.25)], saved.Select(b => b.At));
        Assert.All(saved, b => Assert.Equal(string.Empty, b.Note));
        Assert.Empty(marks.Current);
        Assert.Equal(1, _store.Adds);
    }

    [Fact]
    public async Task APauseThatEndsInAStop_SavesTheMarksToo()
    {
        using var marks = Create();
        var session = _recorder.Start();
        _recorder.Elapsed = S(4);
        _recorder.Pause();
        marks.TryMark(out _);

        _recorder.Stop();
        await marks.PendingFlush;

        Assert.Equal([S(4)], (await _store.GetBookmarksAsync(session.Id, Ct)).Select(b => b.At));
    }

    [Fact]
    public async Task ARecordingWithoutMarks_SavesNothing()
    {
        using var marks = Create();
        _recorder.Start();
        _recorder.Stop();
        await marks.PendingFlush;

        Assert.Equal(0, _store.Adds);
    }

    [Fact]
    public async Task TheNextRecording_StartsWithoutTheMarksOfTheLastOne_AndSavesItsOwn()
    {
        using var marks = Create();
        var first = _recorder.Start();
        _recorder.Elapsed = S(7);
        marks.TryMark(out _);
        _recorder.Stop();
        await marks.PendingFlush;

        var second = _recorder.Start();
        Assert.Empty(marks.Current);
        _recorder.Elapsed = S(2);
        marks.TryMark(out _);
        _recorder.Stop();
        await marks.PendingFlush;

        Assert.Equal([S(7)], (await _store.GetBookmarksAsync(first.Id, Ct)).Select(b => b.At));
        Assert.Equal([S(2)], (await _store.GetBookmarksAsync(second.Id, Ct)).Select(b => b.At));
    }

    [Fact]
    public async Task AFailedSave_IsReported_NeverThrown_AndTheNextRecordingStillSaves()
    {
        using var marks = Create();
        var reported = new List<(SessionId? Session, Exception Error)>();
        marks.SaveFailed += (session, error) => reported.Add((session, error));
        _store.Failure = new IOException("database is locked");
        var first = _recorder.Start();
        marks.TryMark(out _);

        _recorder.Stop();
        await marks.PendingFlush;

        var (failedSession, failure) = Assert.Single(reported);
        Assert.Equal(first.Id, failedSession);
        Assert.Equal("database is locked", failure.Message);
        Assert.Empty(marks.Current);

        _store.Failure = null;
        var second = _recorder.Start();
        Assert.Empty(marks.Current);
        marks.TryMark(out _);
        _recorder.Stop();
        await marks.PendingFlush;
        Assert.Single(await _store.GetBookmarksAsync(second.Id, Ct));
    }

    [Fact]
    public async Task ASaveStillRunning_DoesNotBlockTheStop_AndTheNextRecordingIsSavedAfterIt()
    {
        using var marks = Create();
        var release = new TaskCompletionSource();
        _store.Gate = release.Task;
        var first = _recorder.Start();
        marks.TryMark(out _);

        _recorder.Stop();
        Assert.False(marks.PendingFlush.IsCompleted);
        _store.Gate = null;
        var second = _recorder.Start();
        marks.TryMark(out _);
        _recorder.Stop();
        release.SetResult();
        await marks.PendingFlush;

        Assert.Single(await _store.GetBookmarksAsync(first.Id, Ct));
        Assert.Single(await _store.GetBookmarksAsync(second.Id, Ct));
    }

    [Fact]
    public async Task AMarkThatRacesTheStop_IsNeverLeftBehindUnsaved_AndCurrentIsEmptyOnceIdle()
    {
        using var marks = Create();
        var session = _recorder.Start();
        _recorder.Elapsed = S(1);
        marks.TryMark(out _);
        var started = new ManualResetEventSlim();
        var marking = Task.Run(() =>
        {
            started.Set();
            var accepted = 0;
            for (var i = 0; i < 20_000; i++)
            {
                if (marks.TryMark(out _))
                {
                    accepted++;
                }
            }

            return accepted;
        }, Ct);
        started.Wait(Ct);

        _recorder.Stop();
        var accepted = await marking;
        await marks.PendingFlush;

        // Whatever was accepted before the stop was saved; nothing is waiting for a recording that is over.
        var saved = await _store.GetBookmarksAsync(session.Id, Ct);
        Assert.Equal(accepted + 1, saved.Count);
        Assert.Empty(marks.Current);
        Assert.False(marks.TryMark(out _));
        Assert.Empty(marks.Current);
    }

    [Fact]
    public async Task APauseAfterAStopRequest_DoesNotReopenTheMarks()
    {
        using var marks = Create();
        _recorder.Start();
        _recorder.Force(RecorderState.Stopping);

        Assert.False(marks.TryMark(out _));
        _recorder.Force(RecorderState.Idle);
        await marks.PendingFlush;

        Assert.Equal(0, _store.Adds);
    }

    [Fact]
    public async Task AfterDispose_NoMoreMarksAreSaved()
    {
        var marks = Create();
        _recorder.Start();
        marks.TryMark(out _);
        marks.Dispose();

        _recorder.Stop();
        await marks.PendingFlush;

        Assert.Equal(0, _store.Adds);
    }
}

/// <summary>The same with the real <see cref="Recorder"/> (real time: run alone, like the other recorder tests).</summary>
[Collection(RealtimeCollection.Name)]
public sealed class RecordingMarksRealRecorderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InMemoryBookmarkStore _store = new();

    [Fact]
    public async Task WithTheRealRecorder_ThePausedTimeIsLeftOutOfTheMarks_AndTheyAreSavedOnStop()
    {
        var dir = Path.Combine(Path.GetTempPath(), "murmur-marks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sessions = new InMemorySessionStore(dir);
            var recorder = new Recorder(new SyntheticCapture(), sessions, TimeProvider.System, RecorderSettings.Default);
            using var marks = new RecordingMarks(recorder, _store);

            var session = await recorder.StartAsync(new RecordingOptions(), Ct);
            await Task.Delay(300, Ct);
            marks.TryMark(out var before);
            await recorder.PauseAsync(Ct);
            marks.TryMark(out var atPause);
            await Task.Delay(700, Ct);
            marks.TryMark(out var stillPaused);
            await recorder.ResumeAsync(Ct);
            marks.TryMark(out var afterResume);
            await recorder.StopAsync(Ct);
            await marks.PendingFlush;

            Assert.True(atPause >= before);
            Assert.Equal(atPause, stillPaused);
            Assert.InRange(afterResume - atPause, TimeSpan.Zero, TimeSpan.FromMilliseconds(400));
            var saved = await _store.GetBookmarksAsync(session.Id, Ct);
            Assert.Equal(4, saved.Count);
            Assert.Equal(new[] { before, atPause, stillPaused, afterResume }.Order(), saved.Select(b => b.At));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
