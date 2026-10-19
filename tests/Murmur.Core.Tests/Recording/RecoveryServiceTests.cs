using Murmur.Core.Audio;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Recording;

public sealed class RecoveryServiceTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly InMemorySessionStore _store;

    public RecoveryServiceTests()
    {
        _store = new InMemorySessionStore(_dir.Path);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task InterruptedSession_IsRepairedAndMarkedInterrupted()
    {
        var session = Seed(SessionState.Recording);
        var files = _store.GetFiles(session.Id);
        WriteCrashedWav(files.MicrophoneAudio, flushedSamples: 16_000, unflushedSamples: 16_000);
        WriteCrashedWav(files.SystemAudio, flushedSamples: 16_000, unflushedSamples: 24_000);

        var recovered = Assert.Single(await new RecoveryService(_store).RecoverAsync(Ct));

        Assert.Equal(session.Id, recovered.Id);
        Assert.Equal(SessionState.Interrupted, recovered.State);
        Assert.Equal(TimeSpan.FromSeconds(2.5), recovered.Duration);
        Assert.True(recovered.HasMicrophone);
        Assert.True(recovered.HasSystemAudio);
        Assert.Null(recovered.Error);
        Assert.Equal(recovered, _store.Get(session.Id));
        using var reader = WavReader.Open(files.SystemAudio);
        Assert.Equal(40_000, reader.FrameCount);
        Assert.Equal(80_000, reader.Layout.DeclaredDataBytes);
    }

    [Fact]
    public async Task OnlyOneTrack_IsEnough()
    {
        var session = Seed(SessionState.Recording);
        WriteCrashedWav(_store.GetFiles(session.Id).SystemAudio, flushedSamples: 0, unflushedSamples: 8_000);

        var recovered = Assert.Single(await new RecoveryService(_store).RecoverAsync(Ct));

        Assert.Equal(SessionState.Interrupted, recovered.State);
        Assert.False(recovered.HasMicrophone);
        Assert.True(recovered.HasSystemAudio);
        Assert.Equal(TimeSpan.FromSeconds(0.5), recovered.Duration);
    }

    [Fact]
    public async Task NoAudio_MarksTheSessionFailed()
    {
        var session = Seed(SessionState.Recording);

        var recovered = Assert.Single(await new RecoveryService(_store).RecoverAsync(Ct));

        Assert.Equal(session.Id, recovered.Id);
        Assert.Equal(SessionState.Failed, recovered.State);
        Assert.NotNull(recovered.Error);
    }

    [Fact]
    public async Task FinishedSessions_AreLeftAlone()
    {
        Seed(SessionState.Recorded);
        Seed(SessionState.Ready);
        Seed(SessionState.Interrupted);

        var recovered = await new RecoveryService(_store).RecoverAsync(Ct);

        Assert.Empty(recovered);
        Assert.Empty(_store.Updates);
    }

    [Fact]
    public async Task RecorderSessionKilledMidway_IsRecoveredFromTheWavs()
    {
        // A session the recorder created and flushed, but never stopped (simulated by abandoning the writers).
        var session = Seed(SessionState.Recording);
        var files = _store.GetFiles(session.Id);
        WriteCrashedWav(files.MicrophoneAudio, flushedSamples: 32_000, unflushedSamples: 0);
        WriteCrashedWav(files.SystemAudio, flushedSamples: 32_000, unflushedSamples: 1);

        var recovered = Assert.Single(await new RecoveryService(_store).RecoverAsync(Ct));

        Assert.Equal(TimeSpan.FromSeconds(32_001 / 16_000.0), recovered.Duration);
    }

    [Fact]
    public async Task BusyFile_IsRetriedWithBackoffUntilItOpens()
    {
        var session = Seed(SessionState.Recording);
        var files = _store.GetFiles(session.Id);
        WriteCrashedWav(files.MicrophoneAudio, flushedSamples: 16_000, unflushedSamples: 0);
        var opener = new BusyOpener(busyOpens: 3);
        var waits = new List<TimeSpan>();

        var service = new RecoveryService(_store, null, opener.Open, (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        });
        var recovered = Assert.Single(await service.RecoverAsync(Ct));

        Assert.Equal(SessionState.Interrupted, recovered.State);
        Assert.True(recovered.HasMicrophone);
        Assert.Equal(TimeSpan.FromSeconds(1), recovered.Duration);
        Assert.Equal(RecoveryService.Backoff.Take(3), waits);
        Assert.Empty(service.BusySessions);
    }

    [Fact]
    public async Task AlwaysBusyFile_LeavesTheSessionRecordingAndReportsIt()
    {
        var busy = Seed(SessionState.Recording);
        WriteCrashedWav(_store.GetFiles(busy.Id).MicrophoneAudio, flushedSamples: 16_000, unflushedSamples: 0);
        var waits = new List<TimeSpan>();
        var service = new RecoveryService(
            _store,
            null,
            new BusyOpener(busyOpens: int.MaxValue).Open,
            (delay, _) =>
            {
                waits.Add(delay);
                return Task.CompletedTask;
            });

        var recovered = await service.RecoverAsync(Ct);

        Assert.Empty(recovered);
        Assert.Equal(busy.Id, Assert.Single(service.BusySessions));
        Assert.Equal(SessionState.Recording, _store.Get(busy.Id)?.State);
        Assert.Empty(_store.Updates);
        Assert.Equal(RecoveryService.Backoff.AsEnumerable(), waits);
        Assert.True(RecoveryService.Backoff.Aggregate(TimeSpan.Zero, (a, b) => a + b) < RecoveryService.RetryBudget);

        // The next start finds the file free and recovers the session.
        var next = new RecoveryService(_store);
        var again = Assert.Single(await next.RecoverAsync(Ct));
        Assert.Equal(SessionState.Interrupted, again.State);
        Assert.Empty(next.BusySessions);
    }

    [Fact]
    public async Task OneTrackBusy_DoesNotFailTheSessionEvenIfTheOtherIsMissing()
    {
        var session = Seed(SessionState.Recording);
        var files = _store.GetFiles(session.Id);
        WriteCrashedWav(files.SystemAudio, flushedSamples: 16_000, unflushedSamples: 0);
        var service = new RecoveryService(
            _store,
            null,
            path => path == files.SystemAudio ? throw BusyOpener.SharingViolation() : new FileStream(path, FileMode.Open),
            (_, _) => Task.CompletedTask);

        Assert.Empty(await service.RecoverAsync(Ct));

        Assert.Equal(session.Id, Assert.Single(service.BusySessions));
        Assert.Equal(SessionState.Recording, _store.Get(session.Id)?.State);
    }

    [Fact]
    public async Task InvalidWav_StillCountsAsNoAudio()
    {
        var session = Seed(SessionState.Recording);
        var files = _store.GetFiles(session.Id);
        File.WriteAllBytes(files.MicrophoneAudio, new byte[100]);
        File.WriteAllBytes(files.SystemAudio, "RIFF\0\0\0\0WAVEfmt "u8.ToArray());
        var service = new RecoveryService(_store, null, null, (_, _) => throw new InvalidOperationException("must not wait"));

        var recovered = Assert.Single(await service.RecoverAsync(Ct));

        Assert.Equal(SessionState.Failed, recovered.State);
        Assert.Empty(service.BusySessions);
    }

    [Fact]
    public async Task Cancellation_DuringTheBackoff_StopsRecovery()
    {
        var session = Seed(SessionState.Recording);
        WriteCrashedWav(_store.GetFiles(session.Id).MicrophoneAudio, flushedSamples: 16_000, unflushedSamples: 0);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var service = new RecoveryService(
            _store,
            null,
            new BusyOpener(busyOpens: int.MaxValue).Open,
            (_, token) =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await service.RecoverAsync(cts.Token));

        Assert.Equal(SessionState.Recording, _store.Get(session.Id)?.State);
        Assert.Empty(_store.Updates);
    }

    [Fact]
    public async Task FirstTrackBusy_SecondTrackGetsASingleAttemptWithoutWaiting()
    {
        var session = Seed(SessionState.Recording);
        var files = _store.GetFiles(session.Id);
        WriteCrashedWav(files.MicrophoneAudio, flushedSamples: 16_000, unflushedSamples: 0);
        WriteCrashedWav(files.SystemAudio, flushedSamples: 16_000, unflushedSamples: 0);
        var opens = new Dictionary<string, int>();
        var waits = 0;
        var service = new RecoveryService(
            _store,
            null,
            path =>
            {
                opens[path] = opens.GetValueOrDefault(path) + 1;
                throw BusyOpener.SharingViolation();
            },
            (_, _) =>
            {
                waits++;
                return Task.CompletedTask;
            });

        Assert.Empty(await service.RecoverAsync(Ct));

        Assert.Equal(RecoveryService.Backoff.Length + 1, opens[files.MicrophoneAudio]);
        Assert.Equal(1, opens[files.SystemAudio]);
        Assert.Equal(RecoveryService.Backoff.Length, waits);
        Assert.Equal(session.Id, Assert.Single(service.BusySessions));
    }

    [Fact]
    public async Task RetryBudget_IsSharedByTheWholeRun()
    {
        var time = new ManualTimeProvider();
        var waits = new List<TimeSpan>();
        var opens = 0;
        for (var i = 0; i < 3; i++)
        {
            var session = Seed(SessionState.Recording);
            WriteCrashedWav(_store.GetFiles(session.Id).MicrophoneAudio, flushedSamples: 16_000, unflushedSamples: 0);
        }

        var service = new RecoveryService(
            _store,
            time,
            _ =>
            {
                opens++;
                throw BusyOpener.SharingViolation();
            },
            (delay, _) =>
            {
                waits.Add(delay);
                time.Advance(delay);
                return Task.CompletedTask;
            });

        Assert.Empty(await service.RecoverAsync(Ct));

        Assert.Equal(3, service.BusySessions.Count);
        Assert.Equal(RecoveryService.RetryBudget, waits.Aggregate(TimeSpan.Zero, (a, b) => a + b));

        // 1st session: all 6 waits (7 opens); 2nd: waits until the budget is spent (5 opens); 3rd: one look.
        Assert.Equal(7 + 5 + 1, opens);
    }

    [Fact]
    public async Task RetryBusy_RecoversOnlyTheSessionsThatWereBusy()
    {
        var busy = Seed(SessionState.Recording);
        WriteCrashedWav(_store.GetFiles(busy.Id).MicrophoneAudio, flushedSamples: 16_000, unflushedSamples: 0);
        var isBusy = true;
        var service = new RecoveryService(
            _store,
            null,
            path => isBusy ? throw BusyOpener.SharingViolation() : WavRepair.OpenForRepair(path),
            (_, _) => Task.CompletedTask);
        Assert.Empty(await service.RecoverAsync(Ct));

        // A session recorded after start-up is in the Recording state too: it must never be touched.
        var live = Seed(SessionState.Recording);
        WriteCrashedWav(_store.GetFiles(live.Id).MicrophoneAudio, flushedSamples: 16_000, unflushedSamples: 0);

        Assert.Empty(await service.RetryBusyAsync(Ct));
        Assert.Equal(busy.Id, Assert.Single(service.BusySessions));

        isBusy = false;
        var recovered = Assert.Single(await service.RetryBusyAsync(Ct));

        Assert.Equal(busy.Id, recovered.Id);
        Assert.Equal(SessionState.Interrupted, recovered.State);
        Assert.Empty(service.BusySessions);
        Assert.Equal(SessionState.Recording, _store.Get(live.Id)?.State);
        Assert.Equal(busy.Id, Assert.Single(_store.Updates).Id);
    }

    [Fact]
    public async Task RetryBusy_DropsASessionThatIsNoLongerRecording()
    {
        var busy = Seed(SessionState.Recording);
        WriteCrashedWav(_store.GetFiles(busy.Id).MicrophoneAudio, flushedSamples: 16_000, unflushedSamples: 0);
        var service = new RecoveryService(_store, null, new BusyOpener(int.MaxValue).Open, (_, _) => Task.CompletedTask);
        await service.RecoverAsync(Ct);
        _store.Seed(busy with { State = SessionState.Interrupted });

        Assert.Empty(await service.RetryBusyAsync(Ct));

        Assert.Empty(service.BusySessions);
    }

    [Fact]
    public async Task BackgroundRetry_RunsOnTheScheduleUntilTheSessionIsRecovered()
    {
        var busy = Seed(SessionState.Recording);
        WriteCrashedWav(_store.GetFiles(busy.Id).MicrophoneAudio, flushedSamples: 16_000, unflushedSamples: 0);
        var isBusy = true;
        var longWaits = new List<TimeSpan>();
        var service = new RecoveryService(
            _store,
            null,
            path => isBusy ? throw BusyOpener.SharingViolation() : WavRepair.OpenForRepair(path),
            (delay, _) =>
            {
                if (delay >= TimeSpan.FromSeconds(30))
                {
                    longWaits.Add(delay);
                    isBusy = longWaits.Count < 2; // free from the second pass on
                }

                return Task.CompletedTask;
            });
        await service.RecoverAsync(Ct);

        await service.RetryBusyInBackgroundAsync(Ct);

        Assert.Equal(RecoveryService.BackgroundSchedule.Take(2), longWaits);
        Assert.Equal(SessionState.Interrupted, _store.Get(busy.Id)?.State);
        Assert.Empty(service.BusySessions);
    }

    [Fact]
    public async Task BackgroundRetry_WithNothingBusy_DoesNotWait()
    {
        var service = new RecoveryService(_store, null, null, (_, _) => throw new InvalidOperationException("must not wait"));
        await service.RecoverAsync(Ct);

        await service.RetryBusyInBackgroundAsync(Ct);
    }

    [Fact]
    public async Task BackgroundRetry_StopsOnCancellation()
    {
        var busy = Seed(SessionState.Recording);
        WriteCrashedWav(_store.GetFiles(busy.Id).MicrophoneAudio, flushedSamples: 16_000, unflushedSamples: 0);
        using var cts = new CancellationTokenSource();
        var service = new RecoveryService(
            _store,
            null,
            new BusyOpener(int.MaxValue).Open,
            (delay, token) =>
            {
                if (delay >= TimeSpan.FromSeconds(30))
                {
                    cts.Cancel();
                }

                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        await service.RecoverAsync(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RetryBusyInBackgroundAsync(cts.Token));

        Assert.Equal(SessionState.Recording, _store.Get(busy.Id)?.State);
    }

    /// <summary>Throws a sharing violation for the first <c>busyOpens</c> calls, then opens the file for real.</summary>
    private sealed class BusyOpener(int busyOpens)
    {
        private int _calls;

        /// <summary>What .NET throws for a held file: ERROR_SHARING_VIOLATION on Windows, EWOULDBLOCK (flock) elsewhere.</summary>
        public static IOException SharingViolation() =>
            new(
                "The process cannot access the file because it is being used by another process.",
                OperatingSystem.IsWindows() ? unchecked((int)0x80070020) : OperatingSystem.IsLinux() ? 11 : 35);

        public Stream Open(string path)
        {
            if (_calls++ < busyOpens)
            {
                throw SharingViolation();
            }

            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        }
    }

    private SessionRecord Seed(SessionState state)
    {
        var record = new SessionRecord { Id = SessionId.New(), Title = "t", StartedAt = DateTimeOffset.Now, State = state };
        Directory.CreateDirectory(_store.GetFiles(record.Id).Directory);
        _store.Seed(record);
        return record;
    }

    /// <summary>Writes a WAV like a process that died between two flushes: stale header, extra data, half a sample.</summary>
    private static void WriteCrashedWav(string path, int flushedSamples, int unflushedSamples)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 0);
        var writer = new WavPcm16Writer(stream, leaveOpen: true);
        writer.Append(new float[flushedSamples]);
        writer.Flush();
        writer.Append(new float[unflushedSamples]);
        stream.WriteByte(1);
    }
}
