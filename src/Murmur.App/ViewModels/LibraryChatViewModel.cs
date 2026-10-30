using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>
/// The Ask AI page of the canvas: one conversation (<see cref="Chat"/>) with its scope pills and model chip in the top bar, and the saved
/// chats the sidebar lists (<see cref="Entries"/>). The shell keeps the page for the whole run, so the conversation survives a visit to a
/// cited recording.
/// </summary>
/// <remarks>
/// <para>
/// A conversation is a thread: the first question names it, every finished exchange is saved (see <see cref="ChatViewModel"/>), and a click
/// in the sidebar reopens it with its history. The scope of a thread is fixed when it starts, so the scope pills choose it for a new chat:
/// picking the other pill replaces the conversation in view with a new empty one in that scope (a chat that has messages stays saved and
/// stays in the list).
/// </para>
/// <para>
/// "New chat" is the Ask AI item of the sidebar: it lands on an empty chat, reusing the one in view when it has no messages yet
/// (<see cref="StartNew"/>), so a click never piles up empty chats and never loses a conversation (it stays in the list). Coming back from
/// a cited recording does not start a new one (<see cref="ShellViewModel.ShowLibraryChat"/>).
/// </para>
/// </remarks>
public sealed partial class LibraryChatViewModel : ObservableObject, IFillsFrame, IDisposable
{
    /// <summary>How many saved chats the sidebar lists.</summary>
    public const int ThreadLimit = 50;

    private readonly Func<SessionId?, ChatViewModel> _chatFactory;
    private readonly IChatStore? _store;
    private readonly ISessionStore? _sessions;
    private SessionId? _offered;
    private string? _offeredTitle;
    private IReadOnlyList<ChatThread> _threads = [];
    private readonly List<ChatViewModel> _background = [];
    private int _refreshVersion;
    private bool _rebuildPending;
    private bool _disposed;

    /// <param name="chatFactory">Builds a conversation for a scope (<c>null</c> = the whole library), saving to <paramref name="store"/>.</param>
    /// <param name="store">The saved chats; <c>null</c> = nothing is saved and the sidebar list stays empty.</param>
    /// <param name="sessions">Names the recording of a scoped thread; <c>null</c> = no recording pill for a thread.</param>
    /// <param name="modelChip">The chip at the right of the header.</param>
    public LibraryChatViewModel(Func<SessionId?, ChatViewModel> chatFactory, IChatStore? store = null, ISessionStore? sessions = null, ChatModelChipViewModel? modelChip = null)
    {
        ArgumentNullException.ThrowIfNull(chatFactory);
        _chatFactory = chatFactory;
        _store = store;
        _sessions = sessions;
        ModelChip = modelChip;
        ScopePills = new ChatScopeViewModel(PickScope);
        Chat = Create(null);
        UpdatePills();
        ThreadsTask = RefreshThreadsAsync();
    }

    /// <summary>Raised when the user wants to go back to the library.</summary>
    public event Action? BackRequested;

    /// <summary>A time chip or source card of any conversation was clicked.</summary>
    public event Action<Citation>? CitationActivated;

    /// <summary>The list of saved chats (or which one is open) changed: the sidebar shows <see cref="Entries"/>.</summary>
    public event Action? EntriesChanged;

    /// <summary>The conversation in view. Replaced when another chat is opened or started.</summary>
    [ObservableProperty]
    public partial ChatViewModel Chat { get; private set; }

    /// <summary>The scope pills of the header.</summary>
    public ChatScopeViewModel ScopePills { get; }

    /// <summary>The model chip of the header; <c>null</c> = none.</summary>
    public ChatModelChipViewModel? ModelChip { get; }

    /// <summary>The saved chats, most recently updated first; the open one is <see cref="SidebarEntry.IsActive"/>.</summary>
    public IReadOnlyList<SidebarEntry> Entries { get; private set; } = [];

    public string Title => Strings.Library_AskAll;

    /// <summary>Why a saved chat could not be opened (short, in the UI language); <c>null</c> while nothing is wrong.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>The last refresh of the chat list (for tests).</summary>
    internal Task ThreadsTask { get; private set; } = Task.CompletedTask;

    /// <summary>The last open / rename / delete of a saved chat (for tests).</summary>
    internal Task ActionTask { get; private set; } = Task.CompletedTask;

    public void Dispose()
    {
        _disposed = true;
        Detach(Chat);
        Chat.Dispose();
        foreach (var chat in _background.ToArray())
        {
            Detach(chat);
            chat.Dispose();
        }

        _background.Clear();
        ModelChip?.Dispose();
    }

    /// <summary>
    /// Lands on an empty chat: the one in view when it has no messages (so repeated clicks do not stack empty chats), otherwise a new one.
    /// <paramref name="from"/> is the recording the chat was opened from: it gets the second scope pill (the questions still go to the
    /// whole library until that pill is picked).
    /// </summary>
    public void StartNew(SessionRecord? from = null)
    {
        _offered = from?.Id;
        _offeredTitle = from is null ? null : TitleOf(from);
        Error = null;
        if (!Chat.IsEmpty || Chat.Scope is not null)
        {
            Replace(Create(null));
        }

        UpdatePills();
        RebuildEntries();
    }

    /// <summary>Shows the saved chat with its history, scoped as it was started. Opening the open chat does nothing.</summary>
    public Task OpenThreadAsync(ChatThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return ActionTask = OpenAsync(thread);
    }

    /// <summary>Reads the saved chats again (a recording may have been deleted with its scoped chats); an open chat that is gone is replaced by a new one.</summary>
    public Task RefreshThreadsAsync() => ThreadsTask = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_store is null || _disposed)
        {
            return;
        }

        // Only the newest answer counts: a slow older read must not bring back a deleted row or drop a new one.
        var version = ++_refreshVersion;
        IReadOnlyList<ChatThread> threads;
        try
        {
            threads = await _store.ListThreadsAsync(ThreadLimit, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return;
        }

        if (_disposed || version != _refreshVersion)
        {
            return;
        }

        if (Chat.Thread is { } open && !Chat.IsGenerating && !threads.Any(t => t.Id == open.Id))
        {
            // Deleted behind our back (its recording was deleted): nothing to show any more.
            Replace(Create(null));
            _offered = null;
            _offeredTitle = null;
            UpdatePills();
        }

        _threads = threads;
        RebuildEntries();
    }

    /// <summary>The sidebar rows of the saved chats; the one in view is highlighted.</summary>
    private void RebuildEntries()
    {
        // A row that is being renamed (or asked about deleting) keeps its editor: the list is rebuilt when it is done.
        if (Entries.Any(e => e.IsEditing || e.IsConfirmingDelete))
        {
            _rebuildPending = true;
            return;
        }

        _rebuildPending = false;
        var entries = new List<SidebarEntry>(_threads.Count);
        foreach (var thread in _threads)
        {
            var captured = thread;
            entries.Add(new SidebarEntry(
                thread.Title,
                () => ActionTask = OpenAsync(captured),
                isActive: Chat.Thread?.Id == thread.Id,
                rename: title => ActionTask = RenameAsync(captured, title),
                delete: () => ActionTask = DeleteAsync(captured)));
        }

        foreach (var entry in entries)
        {
            var row = entry;
            entry.PropertyChanged += (_, e) =>
            {
                if (_rebuildPending && e.PropertyName is nameof(SidebarEntry.IsEditing) or nameof(SidebarEntry.IsConfirmingDelete) && !row.IsEditing && !row.IsConfirmingDelete)
                {
                    RebuildEntries();
                }
            };
        }

        Entries = entries;
        EntriesChanged?.Invoke();
    }

    private async Task OpenAsync(ChatThread thread)
    {
        if (_disposed || Chat.Thread?.Id == thread.Id)
        {
            return;
        }

        Error = null;

        // The thread is still being answered in the background: show that conversation, not a stale copy of it.
        var live = _background.FirstOrDefault(c => c.Thread?.Id == thread.Id);
        var chat = live ?? Create(thread.Scope);
        if (live is null)
        {
            try
            {
                await chat.LoadThreadAsync(thread).ConfigureAwait(true);
            }
            catch (Exception)
            {
                Detach(chat);
                chat.Dispose();
                Error = Strings.Chat_OpenFailed;
                return;
            }
        }
        else
        {
            _background.Remove(live);
        }

        string? title = null;
        if (thread.Scope is { } session && _sessions is not null)
        {
            try
            {
                title = (await _sessions.GetSessionAsync(session, CancellationToken.None).ConfigureAwait(true))?.Title;
            }
            catch (Exception)
            {
                title = null;
            }
        }

        _offered = thread.Scope;
        _offeredTitle = thread.Scope is null ? null : string.IsNullOrWhiteSpace(title) ? Strings.Common_UntitledRecording : title;
        Replace(chat);
        UpdatePills();
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RenameAsync(ChatThread thread, string title)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            await _store.RenameThreadAsync(thread.Id, title, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The thread is gone (deleted elsewhere): the refresh below drops its row.
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task DeleteAsync(ChatThread thread)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            await _store.DeleteThreadAsync(thread.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return;
        }

        if (Chat.Thread?.Id == thread.Id)
        {
            _offered = null;
            _offeredTitle = null;
            Replace(Create(null));
            UpdatePills();
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    // ---- scope pills --------------------------------------------------------------------------------------------------------

    /// <summary>Picks the scope of the next questions: the offered recording, or the whole library.</summary>
    private void PickScope(bool session)
    {
        var target = session ? _offered : null;
        if (Chat.Scope == target)
        {
            return;
        }

        // The scope belongs to the thread: a new chat in the other scope, carrying the question that is being typed.
        var question = Chat.Question;
        Replace(Create(target));
        Chat.Question = question;
        UpdatePills();
    }

    private void UpdatePills() => ScopePills.Show(_offered is null ? null : _offeredTitle, sessionScope: Chat.Scope is not null);

    private static string TitleOf(SessionRecord record) =>
        string.IsNullOrWhiteSpace(record.Title) ? Strings.Common_UntitledRecording : record.Title;

    // ---- the conversation ---------------------------------------------------------------------------------------------------

    private ChatViewModel Create(SessionId? scope)
    {
        var chat = _chatFactory(scope);
        chat.CitationActivated += OnCitation;
        chat.ThreadSaved += OnThreadSaved;
        return chat;
    }

    private void Detach(ChatViewModel chat)
    {
        chat.CitationActivated -= OnCitation;
        chat.ThreadSaved -= OnThreadSaved;
    }

    private void Replace(ChatViewModel chat)
    {
        var old = Chat;
        Chat = chat;
        if (!ReferenceEquals(old, chat))
        {
            if (old.IsGenerating)
            {
                // An answer in flight is never cancelled by leaving: it finishes in the background, saves its exchange (its thread joins the
                // list) and is released then.
                _background.Add(old);
                _ = ReleaseWhenDoneAsync(old);
            }
            else
            {
                Detach(old);
                old.Dispose();
            }
        }

        RebuildEntries();
    }

    private async Task ReleaseWhenDoneAsync(ChatViewModel chat)
    {
        try
        {
            await chat.AskTask.ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The answer reports its own failure.
        }

        // It may have been brought back to the screen meanwhile (its thread was opened again).
        if (_background.Remove(chat))
        {
            Detach(chat);
            chat.Dispose();
        }
    }

    private void OnCitation(Citation citation) => CitationActivated?.Invoke(citation);

    private void OnThreadSaved(ChatThread thread) => RefreshThreadsAsync();

    [RelayCommand]
    private void Back() => BackRequested?.Invoke();
}
