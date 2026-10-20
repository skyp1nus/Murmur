using Murmur.App.Localization;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;

namespace Murmur.App.Tests;

public sealed class ShellViewModelTests : IDisposable
{
    // The texts asserted below are Ukrainian; English is the default language (D-039).
    private readonly IDisposable _ukrainian = UiCulture.Override("uk");

    public void Dispose() => _ukrainian.Dispose();

    private static readonly DateTimeOffset Now = new(2026, 10, 18, 15, 0, 0, TimeSpan.Zero);

    private readonly FakeRecorder _recorder = new();
    private readonly FakeCapture _capture = new();
    private readonly FakeSessionStore _store = new();

    private ShellViewModel Create(Func<CancellationToken, Task>? startup = null)
    {
        var library = new LibraryViewModel(_store, new TestTime(Now));
        var recording = new RecordingViewModel(_recorder, _capture, _store, action => action());
        return new ShellViewModel(library, recording, startup);
    }

    [Fact]
    public void StartsOnTheLibrary()
    {
        var shell = Create();

        Assert.Same(shell.Library, shell.CurrentPage);
    }

    [Fact]
    public void NewRecording_NavigatesToTheRecordingScreen()
    {
        var shell = Create();

        shell.Library.NewRecordingCommand.Execute(null);

        Assert.Same(shell.Recording, shell.CurrentPage);
    }

    [Fact]
    public void Back_ReturnsToTheLibrary()
    {
        var shell = Create();
        shell.Library.NewRecordingCommand.Execute(null);

        shell.Recording.BackCommand.Execute(null);

        Assert.Same(shell.Library, shell.CurrentPage);
    }

    [Fact]
    public async Task StoppingARecording_ReturnsToTheReloadedLibrary()
    {
        var shell = Create();
        await shell.InitializeAsync();
        shell.Library.NewRecordingCommand.Execute(null);
        await shell.Recording.StartStopCommand.ExecuteAsync(null);
        var loadsBefore = _store.Queries.Count;

        await shell.Recording.StartStopCommand.ExecuteAsync(null);

        Assert.Same(shell.Library, shell.CurrentPage);
        Assert.True(_store.Queries.Count > loadsBefore);
    }

    [Fact]
    public async Task Initialize_RunsStartupWorkOnTheThreadPoolBeforeLoadingTheLibrary()
    {
        bool? onPoolThread = null;
        var queriesAtStartup = -1;
        var shell = Create(_ =>
        {
            onPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            queriesAtStartup = _store.Queries.Count;
            return Task.CompletedTask;
        });

        await shell.InitializeAsync();

        Assert.True(onPoolThread);
        Assert.Equal(0, queriesAtStartup);
        Assert.Single(_store.Queries);
        Assert.False(shell.Library.IsLoading);
    }

    [Fact]
    public async Task StartupFailure_IsShownInTheLibrary_AndTheLibraryStillLoads()
    {
        var shell = Create(_ => throw new IOException("db corrupt"));

        await shell.InitializeAsync();

        Assert.Single(_store.Queries);
        Assert.False(shell.Library.IsLoading);
        Assert.Equal("Не вдалося відновити перервані записи: db corrupt", shell.Library.Error);
    }
}
