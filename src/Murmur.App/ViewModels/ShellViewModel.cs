using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.Core.Intelligence;
using Murmur.Core.Models;
using Murmur.Core.Recording;
using Murmur.Core.Settings;
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
    private readonly SidebarViewModel? _sidebar;
    private readonly IChatStore? _chatStore;
    private readonly ISessionStore? _sessionStore;
    private readonly ChatModelChipViewModel? _chatModelChip;
    private readonly Func<OnboardingViewModel>? _onboardingFactory;
    private readonly TrialSessionCleaner? _trials;
    private readonly Action<Action> _post;
    private readonly ISettingsStore? _settingsStore;
    private readonly HotkeyHintViewModel _hint = new();
    private OnboardingViewModel? _onboarding;
    private bool _wasCompleted;
    private bool _sessionReturnsToChat;
    private bool _closed;

    /// <param name="sessionFactory">Builds the recording view for a session; <c>null</c> = sessions cannot be opened.</param>
    /// <param name="chatFactory">Builds a chat for a scope (<c>null</c> = the whole library); <c>null</c> = no library-wide chat.</param>
    /// <param name="settings">The Settings screen; <c>null</c> = no Settings page and no sidebar (the pages fill the window).</param>
    /// <param name="onboardingFactory">
    /// Builds the first-run onboarding, only when it is needed (the flag is false, or the Settings clear it); <c>null</c> = none.
    /// It is shown, full window, instead of the library. Needs <paramref name="settingsStore"/>.
    /// </param>
    /// <param name="settingsStore">Where <c>AppSettings.OnboardingCompleted</c> is read and watched.</param>
    /// <param name="trials">Recognises and removes the onboarding's leftover trial recordings.</param>
    /// <param name="post">Marshals the settings store's change event to the UI thread; the UI dispatcher by default.</param>
    /// <param name="chatStore">The saved chats of Ask AI (the sidebar's Chats list); <c>null</c> = chats are not saved.</param>
    /// <param name="sessionStore">Names the recording a saved chat is scoped to.</param>
    /// <param name="chatModelChip">The model chip of the Ask AI header.</param>
    public ShellViewModel(
        LibraryViewModel library,
        RecordingViewModel recording,
        Func<CancellationToken, Task>? startup = null,
        Func<SessionRecord, SessionViewModel>? sessionFactory = null,
        Func<SessionId?, ChatViewModel>? chatFactory = null,
        SettingsViewModel? settings = null,
        Func<OnboardingViewModel>? onboardingFactory = null,
        ISettingsStore? settingsStore = null,
        TrialSessionCleaner? trials = null,
        Action<Action>? post = null,
        IChatStore? chatStore = null,
        ISessionStore? sessionStore = null,
        ChatModelChipViewModel? chatModelChip = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(recording);
        Library = library;
        Recording = recording;
        _startup = startup;
        _sessionFactory = sessionFactory;
        _chatFactory = chatFactory;
        _chatStore = chatStore;
        _sessionStore = sessionStore;
        _chatModelChip = chatModelChip;
        Settings = settings;
        _sidebar = settings is null ? null : new SidebarViewModel(this);
        CurrentPage = library;

        library.CanAskAll = chatFactory is not null;
        library.NewRecordingRequested += ShowRecording;
        library.StartRecordingRequested += OnStartRecordingRequested;
        library.SessionOpened += OnSessionOpened;
        library.SearchResultOpened += OnSearchResultOpened;
        library.AskAllRequested += ShowLibraryChat;
        library.AskRequested += AskLibrary;
        library.PropertyChanged += OnLibraryPropertyChanged;
        recording.BackRequested += ShowLibrary;
        recording.Stopped += OnRecordingStopped;
        recording.PropertyChanged += OnRecordingPropertyChanged;
        _settingsStore = settingsStore;
        if (settingsStore is not null)
        {
            settingsStore.Changed += OnHotkeySettingsChanged;
        }

        UpdateHint();
        UpdateTopBar(library);

        _onboardingFactory = onboardingFactory;
        _trials = trials;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        if (onboardingFactory is not null && settingsStore is not null)
        {
            _wasCompleted = settingsStore.Current.OnboardingCompleted;
            settingsStore.Changed += OnSettingsChanged;
            if (!_wasCompleted)
            {
                ShowOnboarding();
            }
        }
    }

    public LibraryViewModel Library { get; }

    public RecordingViewModel Recording { get; }

    /// <summary>The Settings screen, or <c>null</c> when none is configured.</summary>
    public SettingsViewModel? Settings { get; }

    /// <summary>
    /// The bar above every main screen: the breadcrumb and the record-hotkey hint by default. A screen puts its own content in
    /// <see cref="TopBarViewModel.Leading"/> / <see cref="TopBarViewModel.Trailing"/> (see <see cref="UpdateTopBar"/>).
    /// </summary>
    public TopBarViewModel TopBar { get; } = new();

    /// <summary>
    /// The page still expects the 48 px margin of the old frame (its view draws its own header and spacing for it). A page that was
    /// redrawn after the canvas fills the area under the top bar itself and says so by implementing <see cref="IFillsFrame"/>.
    /// </summary>
    public bool PageUsesFrameInset => CurrentPage is not (IFillsFrame or OnboardingViewModel);

    /// <summary>The first-run onboarding once it has been needed (it is built lazily), else <c>null</c>.</summary>
    public OnboardingViewModel? Onboarding => _onboarding;

    /// <summary>The onboarding is the page on screen: it fills the whole window, with no margin and no sidebar.</summary>
    public bool IsOnboarding => CurrentPage is OnboardingViewModel;

    /// <summary>
    /// The left navigation on every main screen (Library, recording, session, the library-wide chat, Settings); <c>null</c> during
    /// the onboarding, which fills the whole window, and without a Settings screen.
    /// </summary>
    [ObservableProperty]
    public partial SidebarViewModel? Sidebar { get; set; }

    /// <summary>The library-wide chat can be opened (a chat factory is configured).</summary>
    internal bool CanShowLibraryChat => _chatFactory is not null;

    /// <summary>The open recording view, or <c>null</c> while another page is shown.</summary>
    public SessionViewModel? Session { get; private set; }

    /// <summary>The library-wide chat, created on first use and kept (with its history) for the whole run.</summary>
    public LibraryChatViewModel? LibraryChat { get; private set; }

    /// <summary>The <see cref="Library"/>, the <see cref="Recording"/> screen, the open <see cref="Session"/>, the <see cref="LibraryChat"/>, the <see cref="Settings"/> or the <see cref="Onboarding"/>.</summary>
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

        // The Ask AI page saves its chats and checks which cited recordings still exist.
        ChatViewModel CreateSavedChat(SessionId? scope) =>
            new(ai?.Assistant, ai?.Llm, services.Recorder, scope, ai?.ModelsDirectory, store: services.Chats, sessions: services.Sessions, time: services.Time);

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
                chat: CreateChat(record.Id),
                videoPlayback: services.VideoPlayback);
        }

        // Without a store from the composition root (designer, tests) the settings live in memory for the run.
        var store = services.Settings ?? new InMemorySettingsStore();
        var settings = new SettingsViewModel(
            store,
            services.Capture,
            services.Models,
            services.ActiveModelId,
            services.DefaultRecordingsDirectory);
        OnboardingViewModel CreateOnboarding() => new(
            settings,
            store,
            services.Capture,
            services.Recorder,
            services.Sessions,
            services.Models,
            processing,
            services.Transcripts,
            services.Time);

        return new ShellViewModel(
            new LibraryViewModel(services.Sessions, services.Time, processing, keywordSearch: services.Search, assistant: ai?.Assistant, recorder: services.Recorder, transcripts: services.Transcripts, settings: store),
            new RecordingViewModel(services.Recorder, services.Capture, services.Sessions, settings: store),
            services.Startup,
            sessionFactory,
            CreateSavedChat,
            settings,
            CreateOnboarding,
            store,
            new TrialSessionCleaner(services.Sessions, processing),
            chatStore: services.Chats,
            sessionStore: services.Sessions,
            chatModelChip: new ChatModelChipViewModel(() => ActiveChatModelName(services, store), ai?.AccelerationSource));
    }

    /// <summary>The name of the chat model that answers: the active one on disk, else the saved choice, else the first installed one.</summary>
    private static string? ActiveChatModelName(AppServices services, ISettingsStore settings)
    {
        if (services.Models is not { } models)
        {
            return null;
        }

        var chats = models.Catalog.Where(m => m.Kind == ModelKind.Chat).ToArray();
        var id = services.ActiveModelId?.Invoke(ModelKind.Chat) ?? settings.Current.ChatModelId;
        var model = chats.FirstOrDefault(m => m.Id == id)
            ?? chats.FirstOrDefault(m => models.GetStatus(m.Id).State == ModelInstallState.Installed);
        return model?.DisplayName;
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
                startupError = UiText.Format(Strings.Shell_RecoveryFailed, ex.Message);
            }
        }

        // Trial recordings an earlier run left behind (the window closed while they were processed) go first, so they neither
        // show in the library nor make a new user look like an existing one.
        if (_trials is not null)
        {
            await _trials.RemoveLeftoversAsync();
        }

        await Library.LoadAsync();
        if (startupError is not null && Library.Error is null)
        {
            Library.Error = startupError;
        }

        await SkipOnboardingForExistingUserAsync();
    }

    /// <summary>
    /// A settings file from before the onboarding existed has the flag cleared although the user has been recording for a
    /// while: when the library already has sessions of its own (the onboarding's trial recordings do not count) the flag is
    /// set silently and the library stays. Only a first step that nobody has touched yet is replaced (the library loads
    /// quickly, long before anyone gets further).
    /// </summary>
    private async Task SkipOnboardingForExistingUserAsync()
    {
        if (_onboarding is not { } onboarding || CurrentPage != onboarding || onboarding.StepNumber != 1)
        {
            return;
        }

        var existing = _trials is not null ? await _trials.HasOtherSessionsAsync() : Library.HasSessions;
        if (!existing)
        {
            return;
        }

        await onboarding.CompleteSilentlyAsync();
        if (CurrentPage == onboarding)
        {
            ShowLibrary();
        }
    }

    /// <summary>Shows the onboarding from its first step, full window. Ignored without one, and while a recording is running.</summary>
    public void ShowOnboarding()
    {
        if (_closed || _onboardingFactory is null || Recording.State != RecorderState.Idle)
        {
            return;
        }

        CloseSession();
        if (_onboarding is null)
        {
            var created = _onboardingFactory();
            created.Completed += ShowLibrary;
            created.TrialSessionDeleted += OnTrialSessionDeleted;
            _onboarding = created;
        }

        _onboarding.Begin();
        CurrentPage = _onboarding;
    }

    /// <summary>The flag was cleared while the app runs (Settings, "Пройти знайомство ще раз"): the onboarding opens at once.</summary>
    private void OnSettingsChanged(AppSettings settings) => _post(() =>
    {
        var restart = _wasCompleted && !settings.OnboardingCompleted;
        _wasCompleted = settings.OnboardingCompleted;
        if (restart)
        {
            ShowOnboarding();
        }
    });

    /// <summary>
    /// The record hotkey: while idle it opens the recording screen and starts with the saved defaults, while recording it stops
    /// (the stop leads back to the library, as the button does). Ignored while the onboarding is shown (its trial recording
    /// has its own button) and while the recorder is starting or stopping.
    /// </summary>
    internal Task ToggleRecordingFromHotkeyAsync()
    {
        if (CurrentPage is OnboardingViewModel)
        {
            return Task.CompletedTask;
        }

        // On the open recording screen the user's unsaved choices (devices, pills) stay as they are.
        if (Recording.RecorderIsIdle && !ReferenceEquals(CurrentPage, Recording))
        {
            ShowRecording();
        }

        return Recording.ToggleRecordingFromHotkeyAsync();
    }

    /// <summary>The screen hotkey: only while recording, and not during the onboarding's trial recording.</summary>
    internal Task ToggleScreenFromHotkeyAsync() =>
        CurrentPage is OnboardingViewModel ? Task.CompletedTask : Recording.ToggleScreenFromHotkeyAsync();

    /// <summary>The pause hotkey: pauses the running recording or resumes the paused one; nothing otherwise (not during the onboarding's trial).</summary>
    internal Task TogglePauseFromHotkeyAsync() =>
        CurrentPage is OnboardingViewModel ? Task.CompletedTask : Recording.TogglePauseFromHotkeyAsync();

    public void ShowRecording()
    {
        CloseSession();
        Recording.Prepare();
        CurrentPage = Recording;
    }

    public void ShowLibrary()
    {
        CloseSession();
        OnLibraryActivated();
        CurrentPage = Library;
        _ = Library.LoadAsync();
    }

    /// <summary>Opens the Settings screen. Ignored without one.</summary>
    public void ShowSettings()
    {
        if (_closed || Settings is not { } settings)
        {
            return;
        }

        CloseSession();
        settings.Refresh();
        CurrentPage = settings;
    }

    /// <summary>
    /// Opens Ask AI on the conversation that was in view (the one that survives a visit to a cited recording). Ignored without a chat
    /// factory. <see cref="ShowNewChat"/> is the "new chat" of the sidebar.
    /// </summary>
    public void ShowLibraryChat()
    {
        if (_chatFactory is null)
        {
            return;
        }

        CloseSession();
        var page = EnsureLibraryChat();
        page.RefreshThreadsAsync();
        CurrentPage = page;
        PushChats();
    }

    /// <summary>
    /// Opens Ask AI on an empty chat (the sidebar's Ask AI item and the Library's question box): the conversation in view when it is
    /// empty, otherwise a new one; the earlier one stays saved in the Chats list. <paramref name="from"/> is the recording the chat is
    /// opened from; it gets the second scope pill.
    /// </summary>
    public void ShowNewChat(SessionRecord? from = null)
    {
        if (_chatFactory is null)
        {
            return;
        }

        CloseSession();
        var page = EnsureLibraryChat();
        page.StartNew(from);
        page.RefreshThreadsAsync();
        CurrentPage = page;
        PushChats();
    }

    private LibraryChatViewModel EnsureLibraryChat()
    {
        if (LibraryChat is null)
        {
            var page = new LibraryChatViewModel(_chatFactory!, _chatStore, _sessionStore, _chatModelChip);
            page.BackRequested += ShowLibrary;
            page.CitationActivated += OnLibraryCitation;
            page.EntriesChanged += PushChats;
            LibraryChat = page;
        }

        return LibraryChat;
    }

    /// <summary>The sidebar's Chats list is the page's saved chats.</summary>
    private void PushChats()
    {
        if (LibraryChat is { } page)
        {
            _sidebar?.SetChats(page.Entries);
        }
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

    partial void OnCurrentPageChanging(ObservableObject oldValue, ObservableObject newValue)
    {
        // Another page replaces the onboarding: its meters and the trial recording stop (finishing did that already).
        if (oldValue is OnboardingViewModel onboarding && newValue != oldValue)
        {
            onboarding.Suspend();
        }

        // Leaving the Settings stops a hotkey capture that is still waiting (the global hotkeys come back).
        if (oldValue is SettingsViewModel settings && newValue != oldValue)
        {
            settings.Hotkeys.CancelCapture();
        }
    }

    partial void OnCurrentPageChanged(ObservableObject value)
    {
        OnPropertyChanged(nameof(IsOnboarding));
        OnPropertyChanged(nameof(PageUsesFrameInset));
        UpdateTopBar(value);
        if (_sidebar is null)
        {
            return;
        }

        Sidebar = value is OnboardingViewModel ? null : _sidebar;
        _sidebar.Update();
    }

    /// <summary>Puts the breadcrumb and the hint of the page on the top bar. Pages with their own header content replace the parts they own.</summary>
    private void UpdateTopBar(ObservableObject page)
    {
        switch (page)
        {
            case RecordingViewModel:
                TopBar.Leading = new BreadcrumbViewModel(Strings.Nav_Library, new RelayCommand(ShowLibrary), Strings.Library_NewRecording);
                TopBar.Trailing = _hint;
                break;
            case SessionViewModel session:
                TopBar.Leading = new BreadcrumbViewModel(Strings.Nav_Library, new RelayCommand(ShowLibrary), session.Title);
                TopBar.Trailing = null;
                break;
            case LibraryChatViewModel chat:
                // The canvas has the scope pills where the breadcrumb is, and the model chip where the hotkey hint is.
                TopBar.Leading = chat.ScopePills;
                TopBar.Trailing = chat.ModelChip;
                break;
            case SettingsViewModel:
                TopBar.Leading = new BreadcrumbViewModel(Strings.Nav_Settings);
                TopBar.Trailing = null;
                break;
            case OnboardingViewModel:
                TopBar.Leading = null;
                TopBar.Trailing = null;
                break;
            default:
                TopBar.Leading = new BreadcrumbViewModel(Strings.Nav_Library);
                TopBar.Trailing = _hint;
                break;
        }
    }

    private void UpdateHint()
    {
        _hint.Label = Recording.State == RecorderState.Recording ? Strings.Common_Stop : Strings.Hint_Record;
        var gesture = _settingsStore?.Current.RecordHotkey;
        _hint.Gesture = string.IsNullOrWhiteSpace(gesture) ? string.Empty : HotkeyGestures.Display(gesture);
        Library.RecordHotkeyText = _hint.Gesture;
    }

    private void OnHotkeySettingsChanged(AppSettings settings) => _post(UpdateHint);

    private void OnRecordingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordingViewModel.State))
        {
            UpdateHint();
            _sidebar?.Update();
        }
    }

    private void OnLibraryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.Recent))
        {
            _sidebar?.Update();
        }
    }

    /// <summary>"Start recording" of the Library's card: the recording screen opens with the card's switches and starts at once, like the hotkey.</summary>
    private void OnStartRecordingRequested(RecordingSources sources)
    {
        if (Recording.State != RecorderState.Idle)
        {
            ShowRecording();
            return;
        }

        ShowRecording();
        Recording.Microphone = sources.Microphone && Recording.MicrophoneAvailable;
        Recording.SystemAudio = sources.SystemAudio && Recording.SystemAudioAvailable;
        Recording.Screen = sources.Screen && Recording.ScreenAvailable;
        if (Recording.StartStopCommand.CanExecute(null))
        {
            Recording.StartStopCommand.Execute(null);
        }
    }

    /// <summary>
    /// A question typed (or picked) in the Library's "Ask your library" card: Ask AI opens and asks it over the whole library.
    /// Without a chat factory nothing happens.
    /// </summary>
    public void AskLibrary(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (_chatFactory is null || string.IsNullOrWhiteSpace(question))
        {
            return;
        }

        ShowNewChat();
        var chat = LibraryChat!.Chat;
        chat.Question = question.Trim();
        if (chat.AskCommand.CanExecute(null))
        {
            chat.AskCommand.Execute(null);
        }
    }

    private void OnLibraryActivated()
    {
        Library.RefreshDefaults(_settingsStore);
        Library.RefreshRecordingOptions(Recording.MicrophoneAvailable, Recording.SystemAudioAvailable, Recording.ScreenAvailable);
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

    /// <summary>Releases the open session view (its audio and video players); called when the app exits.</summary>
    public void CloseOpenSession() => CloseSession();

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

    /// <summary>A trial recording of the onboarding was removed: the library list on screen drops it.</summary>
    private void OnTrialSessionDeleted()
    {
        if (CurrentPage == Library)
        {
            _ = Library.LoadAsync();
        }
    }

    /// <summary>The window closed: stop what the onboarding runs (meters, the trial recording) and release the Settings and the library.</summary>
    public void OnWindowClosed()
    {
        _closed = true;
        if (_settingsStore is not null)
        {
            _settingsStore.Changed -= OnHotkeySettingsChanged;
            _settingsStore.Changed -= OnSettingsChanged;
        }

        _onboarding?.Suspend();

        // The app ends with its window: release what the screens subscribed to.
        Settings?.Dispose();
        Library.Dispose();
        LibraryChat?.Dispose();
    }

    /// <summary>The window came to the front: the library re-reads sessions that may have been recovered meanwhile.</summary>
    public void OnWindowActivated() => Library.OnWindowActivated();
}
