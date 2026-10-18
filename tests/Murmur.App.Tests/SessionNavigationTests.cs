using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;

namespace Murmur.App.Tests;

public sealed class SessionNavigationTests : IDisposable
{
    private readonly SessionFixture _fixture = new();
    private readonly FakeRecorder _recorder = new();
    private readonly FakeCapture _capture = new();
    private readonly FakeSessionStore _store = new();

    public void Dispose() => _fixture.Dispose();

    private ShellViewModel CreateShell(bool withFactory = true)
    {
        var library = new LibraryViewModel(_store, new TestTime(UiHarness.Now), _fixture.Processing, action => action());
        var recording = new RecordingViewModel(_recorder, _capture, _store, action => action());
        return new ShellViewModel(
            library,
            recording,
            sessionFactory: withFactory
                ? record => new SessionViewModel(record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action())
                : null);
    }

    [Fact]
    public async Task ClickingALibraryRow_ShowsTheSession()
    {
        _store.Add(_fixture.Record);
        _fixture.UseShortTranscript();
        var shell = CreateShell();
        await shell.Library.LoadAsync();

        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;

        var session = Assert.IsType<SessionViewModel>(shell.CurrentPage);
        Assert.Same(session, shell.Session);
        Assert.Equal(_fixture.Record.Title, session.Title);
        Assert.Equal(TranscriptStatus.Available, session.Status);
    }

    [Fact]
    public async Task Back_ReturnsToTheLibrary_AndReleasesThePlayer()
    {
        _store.Add(_fixture.Record);
        var shell = CreateShell();
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;
        var loadsBefore = _store.Queries.Count;

        shell.Session!.BackCommand.Execute(null);

        Assert.Same(shell.Library, shell.CurrentPage);
        Assert.Null(shell.Session);
        Assert.True(_fixture.Player.Disposed);
        Assert.True(_store.Queries.Count > loadsBefore, "The library should reload on return.");
    }

    [Fact]
    public async Task OpeningAnotherPage_ClosesTheOpenSession()
    {
        _store.Add(_fixture.Record);
        var shell = CreateShell();
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;

        shell.ShowRecording();

        Assert.Same(shell.Recording, shell.CurrentPage);
        Assert.True(_fixture.Player.Disposed);
    }

    [Fact]
    public async Task WithoutASessionFactory_ClickingARowDoesNothing()
    {
        _store.Add(_fixture.Record);
        var shell = CreateShell(withFactory: false);
        await shell.Library.LoadAsync();

        shell.Library.SelectedSession = shell.Library.Sessions[0];

        Assert.Same(shell.Library, shell.CurrentPage);
        Assert.Null(shell.Session);
    }

    [Fact]
    public async Task Create_WithPlaybackAndTranscripts_CanOpenASession()
    {
        _store.Add(_fixture.Record);
        var services = new AppServices(
            _recorder,
            _capture,
            _store,
            new TestTime(UiHarness.Now),
            Transcripts: _fixture.Transcripts,
            Playback: new FakePlayback(_fixture.Player),
            Processing: _fixture.Processing);
        var shell = ShellViewModel.Create(services);
        await shell.Library.LoadAsync();

        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;

        Assert.NotNull(shell.Session);
    }

    private sealed class FakePlayback(FakeAudioPlayer player) : Murmur.Core.Capture.IAudioPlayback
    {
        public Murmur.Core.Capture.IAudioPlayer CreatePlayer() => player;
    }
}
