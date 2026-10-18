using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.App.Services;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>
/// Owns the screens and the navigation between them. The window renders <see cref="CurrentPage"/> through
/// data templates keyed by view-model type (no reflection-based view locator).
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task>? _startup;
    private readonly Func<SessionRecord, SessionViewModel>? _sessionFactory;

    /// <param name="sessionFactory">Builds the recording view for a session; <c>null</c> = sessions cannot be opened.</param>
    public ShellViewModel(
        LibraryViewModel library,
        RecordingViewModel recording,
        Func<CancellationToken, Task>? startup = null,
        Func<SessionRecord, SessionViewModel>? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(recording);
        Library = library;
        Recording = recording;
        _startup = startup;
        _sessionFactory = sessionFactory;
        CurrentPage = library;

        library.NewRecordingRequested += ShowRecording;
        library.SessionOpened += OpenSession;
        recording.BackRequested += ShowLibrary;
        recording.Stopped += OnRecordingStopped;
    }

    public LibraryViewModel Library { get; }

    public RecordingViewModel Recording { get; }

    /// <summary>The open recording view, or <c>null</c> while another page is shown.</summary>
    public SessionViewModel? Session { get; private set; }

    /// <summary>The <see cref="Library"/>, the <see cref="Recording"/> screen or the open <see cref="Session"/>.</summary>
    [ObservableProperty]
    public partial ObservableObject CurrentPage { get; set; }

    /// <summary>Builds the screens from the services handed over by the composition root.</summary>
    public static ShellViewModel Create(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var processing = services.Processing;
        Func<SessionRecord, SessionViewModel>? sessionFactory = null;
        if (services.Transcripts is { } transcripts && services.Playback is { } playback)
        {
            sessionFactory = record => new SessionViewModel(
                record,
                services.Sessions.GetFiles(record.Id),
                transcripts,
                playback.CreatePlayer(),
                processing ?? NullSessionProcessing.Instance,
                services.Time);
        }

        return new ShellViewModel(
            new LibraryViewModel(services.Sessions, services.Time, processing),
            new RecordingViewModel(services.Recorder, services.Capture, services.Sessions),
            services.Startup,
            sessionFactory);
    }

    /// <summary>
    /// Runs the startup work (crash recovery) off the UI thread, then loads the library. A startup failure is shown
    /// in the library instead of crashing the app.
    /// </summary>
    public async Task InitializeAsync()
    {
        string? startupError = null;
        if (_startup is { } startup)
        {
            try
            {
                await Task.Run(() => startup(CancellationToken.None));
            }
            catch (Exception ex)
            {
                startupError = "Не вдалося відновити перервані записи: " + ex.Message;
            }
        }

        await Library.LoadAsync();
        if (startupError is not null && Library.Error is null)
        {
            Library.Error = startupError;
        }
    }

    public void ShowRecording()
    {
        CloseSession();
        Recording.Prepare();
        CurrentPage = Recording;
    }

    public void ShowLibrary()
    {
        CloseSession();
        CurrentPage = Library;
        _ = Library.LoadAsync();
    }

    /// <summary>Opens the recording view of a session (player + transcript). Ignored when no session factory is configured.</summary>
    public void OpenSession(SessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_sessionFactory is null)
        {
            return;
        }

        CloseSession();
        var session = _sessionFactory(record);
        session.BackRequested += ShowLibrary;
        Session = session;
        CurrentPage = session;
        SessionLoadTask = session.LoadAsync();
    }

    /// <summary>The load of the open session (for tests that need to await it).</summary>
    internal Task SessionLoadTask { get; private set; } = Task.CompletedTask;

    private void CloseSession()
    {
        if (Session is not { } session)
        {
            return;
        }

        session.BackRequested -= ShowLibrary;
        Session = null;
        session.Dispose();
    }

    private void OnRecordingStopped(SessionRecord session) => ShowLibrary();
}
