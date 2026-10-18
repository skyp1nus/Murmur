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
