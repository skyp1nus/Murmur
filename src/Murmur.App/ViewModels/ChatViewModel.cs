using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Intelligence;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>Whether questions can be asked right now.</summary>
public enum ChatAvailability
{
    Ready,

    /// <summary>No assistant, or the models are missing: how-to-install card.</summary>
    NotInstalled,

    /// <summary>A recording is running: models are never loaded while recording.</summary>
    PausedForRecording,
}

/// <summary>
/// "Запитати AI": questions about one session (<see cref="Scope"/> set) or the whole library (<c>null</c>), answered
/// by <see cref="IAssistant.AskAsync"/>. The answer streams into the last bubble as it arrives; the citations become
/// timestamp chips, and clicking one raises <see cref="CitationActivated"/> (the owner seeks or opens the session).
/// </summary>
/// <remarks>
/// The history lives as long as this view model: the recording view keeps one per open session, the shell one for the
/// library-wide chat. Runs on the UI thread; recorder events are marshalled with the <c>post</c> callback.
/// </remarks>
public sealed partial class ChatViewModel : ObservableObject, IDisposable
{
    /// <summary>At most this many earlier turns (questions and answers) are sent with a new question.</summary>
    public const int MaxHistoryTurns = 6;

    public static string NotInstalledTitle => Strings.Chat_NotInstalledTitle;

    public static string PausedTitle => Strings.Chat_PausedTitle;

    public static string CancelledText => Strings.Chat_Cancelled;

    public static string InterruptedByRecordingText => Strings.Chat_Interrupted;

    /// <summary>"Не вдалося отримати відповідь: <paramref name="reason"/>" in the UI language.</summary>
    public static string FailedText(string reason) => UiText.Format(Strings.Chat_Failed, reason);

    private readonly IAssistant? _assistant;
    private readonly ILlm? _llm;
    private readonly IRecorder? _recorder;
    private readonly string _modelsDirectory;
    private readonly Action<Action> _post;
    private readonly IChatStore? _store;
    private readonly ISessionStore? _sessions;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _generation;
    private int _storedCount;
    private bool _disposed;

    /// <param name="assistant">Answers the questions; <c>null</c> = AI not wired, the chat shows the install card.</param>
    /// <param name="llm">Tells whether the models are installed.</param>
    /// <param name="recorder">While it is not idle the chat is paused.</param>
    /// <param name="scope">One session, or <c>null</c> for the whole library.</param>
    /// <param name="modelsDirectory">Shown in the install hint.</param>
    /// <param name="post">Marshals a callback to the UI thread; recorder events arrive on its own thread.</param>
    /// <param name="store">Where the exchanges are saved as a thread (the first question names it); <c>null</c> = nothing is saved.</param>
    /// <param name="sessions">Tells which cited recordings still exist and what they are called (Sources cards, chips of another recording); <c>null</c> = every citation is a link.</param>
    /// <param name="time">Stamps the saved messages.</param>
    public ChatViewModel(
        IAssistant? assistant,
        ILlm? llm,
        IRecorder? recorder,
        SessionId? scope,
        string? modelsDirectory = null,
        Action<Action>? post = null,
        IChatStore? store = null,
        ISessionStore? sessions = null,
        TimeProvider? time = null)
    {
        _store = store;
        _sessions = sessions;
        _time = time ?? TimeProvider.System;
        _assistant = assistant;
        _llm = llm;
        _recorder = recorder;
        _modelsDirectory = modelsDirectory ?? string.Empty;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        Scope = scope;
        Messages.CollectionChanged += (_, _) => OnMessagesChanged();
        if (recorder is not null)
        {
            recorder.StateChanged += OnRecorderStateChanged;
        }

        RefreshAvailability();
    }

    /// <summary>A chip was clicked: play <see cref="Citation.Session"/> from <see cref="Citation.At"/>.</summary>
    public event Action<Citation>? CitationActivated;

    /// <summary>New content arrived at the bottom of the list (the view keeps it in sight).</summary>
    public event Action? ScrollToEndRequested;

    /// <summary>Copy was pressed under an answer: the owner puts the text on the clipboard.</summary>
    public event Action<string>? CopyRequested;

    /// <summary>The exchange was saved (the thread was created, or got a message): the chat list refreshes.</summary>
    public event Action<ChatThread>? ThreadSaved;

    public SessionId? Scope { get; }

    /// <summary>The saved thread of this conversation; <c>null</c> until the first exchange has been saved (or when nothing is saved).</summary>
    public ChatThread? Thread { get; private set; }

    /// <summary>The conversation has no message yet.</summary>
    public bool IsEmpty => Messages.Count == 0;

    /// <summary>The conversation, oldest first.</summary>
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    public partial string Question { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditQuestion))]
    public partial bool IsGenerating { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateCommand))]
    [NotifyPropertyChangedFor(nameof(ShowNotice))]
    [NotifyPropertyChangedFor(nameof(NoticeTitle))]
    [NotifyPropertyChangedFor(nameof(NoticeText))]
    [NotifyPropertyChangedFor(nameof(CanCheckAgain))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    [NotifyPropertyChangedFor(nameof(CanEditQuestion))]
    public partial ChatAvailability Availability { get; private set; }

    public string PlaceholderText => Scope is null ? Strings.Chat_PlaceholderLibrary : Strings.Chat_PlaceholderSession;

    public string EmptyTitle => Scope is null ? Strings.Chat_EmptyTitleLibrary : Strings.Chat_EmptyTitleSession;

    public string EmptyText => Scope is null
        ? Strings.Chat_EmptyTextLibrary
        : Strings.Chat_EmptyTextSession;

    /// <summary>The install or pause card is on screen.</summary>
    public bool ShowNotice => Availability != ChatAvailability.Ready;

    public string NoticeTitle => Availability switch
    {
        ChatAvailability.NotInstalled => NotInstalledTitle,
        ChatAvailability.PausedForRecording => PausedTitle,
        _ => string.Empty,
    };

    public string NoticeText => Availability switch
    {
        ChatAvailability.NotInstalled => _modelsDirectory.Length > 0
            ? UiText.Format(Strings.Chat_NoticeNotInstalledWithFolder, _modelsDirectory)
            : Strings.Chat_NoticeNotInstalled,
        ChatAvailability.PausedForRecording => Strings.Chat_NoticePaused,
        _ => string.Empty,
    };

    public bool CanCheckAgain => Availability == ChatAvailability.NotInstalled;

    /// <summary>The suggestion shown in an empty, usable chat.</summary>
    public bool ShowEmptyHint => Availability == ChatAvailability.Ready && Messages.Count == 0;

    public bool CanEditQuestion => Availability == ChatAvailability.Ready && !IsGenerating;

    /// <summary>The running question (for tests); completes when the answer has finished, failed or was cancelled.</summary>
    internal Task AskTask { get; private set; } = Task.CompletedTask;

    /// <summary>Re-reads whether the models are installed and whether a recording is running.</summary>
    [RelayCommand]
    public void RefreshAvailability()
    {
        if (_disposed)
        {
            return;
        }

        Availability = _assistant is null || _llm is null || _llm.Status == LlmStatus.NotInstalled
            ? ChatAvailability.NotInstalled
            : _recorder is not null && _recorder.State != RecorderState.Idle
                ? ChatAvailability.PausedForRecording
                : ChatAvailability.Ready;
    }

    /// <summary>Stops a running answer and stops following the recorder.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _generation?.Cancel();
        if (_recorder is not null)
        {
            _recorder.StateChanged -= OnRecorderStateChanged;
        }
    }

    private bool CanAsk() =>
        Availability == ChatAvailability.Ready && !IsGenerating && !string.IsNullOrWhiteSpace(Question);

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private void Ask()
    {
        RefreshAvailability();
        if (!CanAsk())
        {
            return;
        }

        var question = ChatMessageViewModel.User(question: Question.Trim());
        AskTask = RunAskAsync(question, regenerate: false);
    }

    private bool CanRegenerate() =>
        Availability == ChatAvailability.Ready && !IsGenerating && Messages.Count >= 2 && Messages[^1].IsAssistant && Messages[^2].IsUser;

    /// <summary>Asks the last question again: the new answer replaces the last one (in the chat and in the saved thread).</summary>
    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private void Regenerate()
    {
        RefreshAvailability();
        if (!CanRegenerate())
        {
            return;
        }

        var question = Messages[^2];
        Messages.RemoveAt(Messages.Count - 1);
        AskTask = RunAskAsync(question, regenerate: true);
    }

    [RelayCommand]
    private void CopyAnswer(ChatMessageViewModel? message)
    {
        if (message is { IsAssistant: true, HasText: true })
        {
            CopyRequested?.Invoke(message.Text);
        }
    }

    private bool CanCancel() => IsGenerating;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _generation?.Cancel();

    private async Task RunAskAsync(ChatMessageViewModel user, bool regenerate)
    {
        var history = BuildHistory();
        var answer = ChatMessageViewModel.Answer();
        Attach(answer);
        if (!regenerate)
        {
            Attach(user);
            Messages.Add(user);
            Question = string.Empty;
        }

        Messages.Add(answer);
        var question = user.Text;
        var generation = new CancellationTokenSource();
        _generation = generation;
        IsGenerating = true;
        ScrollToEndRequested?.Invoke();
        try
        {
            var request = new AskRequest(question, Scope, history);
            IReadOnlyList<Citation> citations = [];
            await foreach (var update in OnThreadPool(_assistant!.AskAsync(request, generation.Token), generation.Token).ConfigureAwait(true))
            {
                if (_disposed)
                {
                    break;
                }

                if (update.TextDelta.Length > 0)
                {
                    answer.Append(update.TextDelta);
                    ScrollToEndRequested?.Invoke();
                }

                if (update.IsFinal)
                {
                    citations = update.Citations;
                }
            }

            if (generation.IsCancellationRequested)
            {
                answer.Stop(CancelledText);
            }
            else
            {
                var titles = await ResolveTitlesAsync(citations).ConfigureAwait(true);
                answer.SetCitations(citations, OnCitation, titles);
                answer.Complete();
                ScrollToEndRequested?.Invoke();
                await SaveExchangeAsync(user, answer).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (generation.IsCancellationRequested)
        {
            answer.Stop(CancelledText);
        }
        catch (Exception ex)
        {
            // The usual causes: a recording started (the models were stopped under the request) or the files vanished.
            RefreshAvailability();
            answer.Stop(Availability switch
            {
                ChatAvailability.PausedForRecording => InterruptedByRecordingText,
                ChatAvailability.NotInstalled => FailedText(Strings.Chat_ModelsNotFound),
                _ => FailedText(ex.Message),
            });
        }
        finally
        {
            _generation = null;
            generation.Dispose();
            IsGenerating = false;
        }
    }

    /// <summary>Wires the Copy and Regenerate buttons of a message to this chat.</summary>
    private void Attach(ChatMessageViewModel message)
    {
        message.CopyCommand = new RelayCommand(() => CopyAnswer(message));
        message.RegenerateCommand = RegenerateCommand;
    }

    private void OnMessagesChanged()
    {
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(IsEmpty));
        for (var i = 0; i < Messages.Count; i++)
        {
            Messages[i].IsLastAnswer = i == Messages.Count - 1 && Messages[i].IsAssistant;
        }

        RegenerateCommand.NotifyCanExecuteChanged();
    }

    // ---- saved threads --------------------------------------------------------------------------------------------------

    /// <summary>The longest thread title taken from a question; a longer one is cut and ends with an ellipsis.</summary>
    public const int MaxTitleLength = 60;

    /// <summary>The name of a thread that starts with <paramref name="question"/>: its first line, trimmed, at most <see cref="MaxTitleLength"/> characters.</summary>
    internal static string TitleFor(string question)
    {
        var line = question.AsSpan().Trim();
        var newline = line.IndexOfAny('\r', '\n');
        if (newline >= 0)
        {
            line = line[..newline].TrimEnd();
        }

        return line.Length <= MaxTitleLength ? line.ToString() : string.Concat(line[..(MaxTitleLength - 1)].TrimEnd(), "…");
    }

    /// <summary>
    /// Saves a finished exchange: creates the thread on the first one (its title is the question), appends the question and the answer, or,
    /// after Regenerate, replaces the saved answer. A store that fails does not break the chat: the exchange stays on screen, unsaved.
    /// </summary>
    private async Task SaveExchangeAsync(ChatMessageViewModel user, ChatMessageViewModel answer)
    {
        if (_store is null || _disposed)
        {
            return;
        }

        try
        {
            Thread ??= await _store.CreateThreadAsync(TitleFor(user.Text), Scope, CancellationToken.None).ConfigureAwait(true);
            var thread = Thread;
            if (user.StoredIndex is null)
            {
                user.StoredIndex = _storedCount;
                await _store.AppendMessageAsync(new ChatMessage(thread.Id, _storedCount, TurnRole.User, user.Text, [], _time.GetUtcNow()), CancellationToken.None).ConfigureAwait(true);
            }

            answer.StoredIndex = user.StoredIndex + 1;
            await _store.AppendMessageAsync(
                new ChatMessage(thread.Id, answer.StoredIndex.Value, TurnRole.Assistant, answer.Text, answer.CitationData, _time.GetUtcNow()),
                CancellationToken.None).ConfigureAwait(true);
            _storedCount = answer.StoredIndex.Value + 1;
            ThreadSaved?.Invoke(thread);
        }
        catch (Exception)
        {
            // Saving is best effort: a locked or full database must not turn a good answer into an error.
            user.StoredIndex = null;
            answer.StoredIndex = null;
        }
    }

    /// <summary>Shows a saved thread: its messages (answers with their chips and sources) and the thread as the one to append to.</summary>
    public async Task LoadThreadAsync(ChatThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (_store is null)
        {
            return;
        }

        var saved = await _store.GetMessagesAsync(thread.Id, CancellationToken.None).ConfigureAwait(true);
        var citations = new List<Citation>();
        foreach (var message in saved)
        {
            citations.AddRange(message.Citations);
        }

        var titles = await ResolveTitlesAsync(citations).ConfigureAwait(true);
        Messages.Clear();
        foreach (var message in saved)
        {
            var restored = ChatMessageViewModel.Restore(message, OnCitation, titles);
            Attach(restored);
            Messages.Add(restored);
        }

        Thread = thread;
        _storedCount = saved.Count;
    }

    /// <summary>
    /// The title of every cited recording that still exists; a recording that was deleted is present with <c>null</c> (its chips are no
    /// longer links). <c>null</c> without a session store: every citation counts as alive.
    /// </summary>
    private async Task<IReadOnlyDictionary<SessionId, string?>?> ResolveTitlesAsync(IReadOnlyList<Citation> citations)
    {
        if (_sessions is null)
        {
            return null;
        }

        var titles = new Dictionary<SessionId, string?>();
        foreach (var citation in citations)
        {
            if (titles.ContainsKey(citation.Session))
            {
                continue;
            }

            try
            {
                var session = await _sessions.GetSessionAsync(citation.Session, CancellationToken.None).ConfigureAwait(true);
                titles[citation.Session] = session?.Title;
            }
            catch (Exception)
            {
                // A store that cannot answer must not hide a good answer: treat the recording as there.
                titles[citation.Session] = string.Empty;
            }
        }

        return titles;
    }

    /// <summary>
    /// Advances <paramref name="source"/> on the thread pool and hands every item back on the caller's context. The
    /// assistant does synchronous work before its first real await (SQLite keyword search, starting llama-server),
    /// which must not run on the UI thread.
    /// </summary>
    private static async IAsyncEnumerable<T> OnThreadPool<T>(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (await Task.Run(() => enumerator.MoveNextAsync().AsTask(), cancellationToken).ConfigureAwait(true))
            {
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(true);
        }
    }

    private List<ChatTurn> BuildHistory()
    {
        var turns = new List<ChatTurn>();
        for (var i = 0; i + 1 < Messages.Count; i += 2)
        {
            var question = Messages[i];
            var answer = Messages[i + 1];
            if (question.IsUser && answer.IsAssistant && answer.Succeeded)
            {
                turns.Add(new ChatTurn(TurnRole.User, question.Text));
                turns.Add(new ChatTurn(TurnRole.Assistant, answer.Text));
            }
        }

        return turns.Count > MaxHistoryTurns ? turns.GetRange(turns.Count - MaxHistoryTurns, MaxHistoryTurns) : turns;
    }

    private void OnCitation(Citation citation) => CitationActivated?.Invoke(citation);

    private void OnRecorderStateChanged(RecorderState state) => _post(RefreshAvailability);
}
