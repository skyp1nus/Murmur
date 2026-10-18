using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.App.Services;
using Murmur.Core.Intelligence;
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
    private readonly Func<SessionId?, ChatViewModel>? _chatFactory;
    private bool _sessionReturnsToChat;

    /// <param name="sessionFactory">Builds the recording view for a session; <c>null</c> = sessions cannot be opened.</param>
    /// <param name="chatFactory">Builds a chat for a scope (<c>null</c> = the whole library); <c>null</c> = no library-wide chat.</param>
    public ShellViewModel(
        LibraryViewModel library,
        RecordingViewModel recording,
        Func<CancellationToken, Task>? startup = null,
        Func<SessionRecord, SessionViewModel>? sessionFactory = null,
        Func<SessionId?, ChatViewModel>? chatFactory = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(recording);
        Library = library;
        Recording = recording;
        _startup = startup;
        _sessionFactory = sessionFactory;
        _chatFactory = chatFactory;
        CurrentPage = library;

        library.CanAskAll = chatFactory is not null;
        library.NewRecordingRequested += ShowRecording;
        library.SessionOpened += OnSessionOpened;
        library.SearchResultOpened += OnSearchResultOpened;
        library.AskAllRequested += ShowLibraryChat;
        recording.BackRequested += ShowLibrary;
        recording.Stopped += OnRecordingStopped;
    }

    public LibraryViewModel Library { get; }

    public RecordingViewModel Recording { get; }

    /// <summary>The open recording view, or <c>null</c> while another page is shown.</summary>
    public SessionViewModel? Session { get; private set; }

    /// <summary>The library-wide chat, created on first use and kept (with its history) for the whole run.</summary>
    public LibraryChatViewModel? LibraryChat { get; private set; }

    /// <summary>The <see cref="Library"/>, the <see cref="Recording"/> screen, the open <see cref="Session"/> or the <see cref="LibraryChat"/>.</summary>
    [ObservableProperty]
    public partial ObservableObject CurrentPage { get; set; }

    /// <summary>The load of the open session, including the initial seek (for tests that need to await it).</summary>
    internal Task SessionLoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>The last "open the cited session" request (for tests).</summary>
    internal Task NavigationTask { get; private set; } = Task.CompletedTask;

    /// <summary>Builds the screens from the services handed over by the composition root.</summary>
    public static ShellViewModel Create(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var processing = services.Processing;
        var ai = services.Ai;
        ChatViewModel CreateChat(SessionId? scope) =>
            new(ai?.Assistant, ai?.Llm, services.Recorder, scope, ai?.ModelsDirectory);

        Func<SessionRecord, SessionViewModel>? sessionFactory = null;
        if (services.Transcripts is { } transcripts && services.Playback is { } playback)
        {
            sessionFactory = record => new SessionViewModel(
                record,
                services.Sessions.GetFiles(record.Id),
                transcripts,
                playback.CreatePlayer(),
                processing ?? NullSessionProcessing.Instance,
                services.Time,
                chat: CreateChat(record.Id));
        }

        return new ShellViewModel(
            new LibraryViewModel(services.Sessions, services.Time, processing, keywordSearch: services.Search, assistant: ai?.Assistant),
            new RecordingViewModel(services.Recorder, services.Capture, services.Sessions),
            services.Startup,
            sessionFactory,
            CreateChat);
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

    /// <summary>Opens the library-wide chat ("Запитати AI по всіх записах"). Ignored without a chat factory.</summary>
    public void ShowLibraryChat()
    {
        if (_chatFactory is null)
        {
            return;
        }

        CloseSession();
        if (LibraryChat is null)
        {
            var chat = new LibraryChatViewModel(_chatFactory(null));
            chat.BackRequested += ShowLibrary;
            chat.Chat.CitationActivated += OnLibraryCitation;
            LibraryChat = chat;
        }

        LibraryChat.Chat.RefreshAvailability();
        CurrentPage = LibraryChat;
    }

    /// <summary>
    /// Opens the recording view of a session (player + transcript). With <paramref name="at"/> the player is moved
    /// there once the audio is open, and plays when <paramref name="play"/>. Ignored when no session factory is configured.
    /// </summary>
    public void OpenSession(SessionRecord record, TimeSpan? at = null, bool play = false)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_sessionFactory is null)
        {
            return;
        }

        var returnsToChat = CurrentPage is LibraryChatViewModel || (CurrentPage is SessionViewModel && _sessionReturnsToChat);
        CloseSession();
        var session = _sessionFactory(record);
        session.BackRequested += OnSessionBack;
        session.OpenSessionRequested += OnOpenSessionRequested;
        _sessionReturnsToChat = returnsToChat;
        Session = session;
        CurrentPage = session;
        SessionLoadTask = LoadAsync(session, at, play);
    }

    /// <summary>Opens a session by id at a moment (a chip of the library-wide chat); nothing happens when it no longer exists.</summary>
    public Task OpenSessionAtAsync(SessionId id, TimeSpan at, bool play)
    {
        NavigationTask = OpenAtAsync(id, at, play);
        return NavigationTask;
    }

    private static async Task LoadAsync(SessionViewModel session, TimeSpan? at, bool play)
    {
        await session.LoadAsync().ConfigureAwait(true);
        if (at is not { } position)
        {
            return;
        }

        if (play)
        {
            session.PlayFrom(position);
        }
        else
        {
            session.SeekTo(position);
        }
    }

    private async Task OpenAtAsync(SessionId id, TimeSpan at, bool play)
    {
        if (Session is { } open && open.Id == id)
        {
            open.PlayFrom(at);
            return;
        }

        var record = await Library.FindSessionAsync(id).ConfigureAwait(true);
        if (record is not null)
        {
            OpenSession(record, at, play);
        }
    }

    private void OnSessionOpened(SessionRecord record) => OpenSession(record);

    private void OnSearchResultOpened(SessionRecord record, TimeSpan? at) => OpenSession(record, at, play: false);

    private void OnLibraryCitation(Citation citation) => _ = OpenSessionAtAsync(citation.Session, citation.At, play: true);

    private void OnOpenSessionRequested(SessionId id, TimeSpan at) => _ = OpenSessionAtAsync(id, at, play: true);

    private void OnSessionBack()
    {
        if (_sessionReturnsToChat && LibraryChat is not null)
        {
            ShowLibraryChat();
        }
        else
        {
            ShowLibrary();
        }
    }

    private void CloseSession()
    {
        if (Session is not { } session)
        {
            return;
        }

        session.BackRequested -= OnSessionBack;
        session.OpenSessionRequested -= OnOpenSessionRequested;
        Session = null;
        _sessionReturnsToChat = false;
        session.Dispose();
    }

    private void OnRecordingStopped(SessionRecord session) => ShowLibrary();
}
