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
    private CancellationTokenSource? _generation;
    private bool _disposed;

    /// <param name="assistant">Answers the questions; <c>null</c> = AI not wired, the chat shows the install card.</param>
    /// <param name="llm">Tells whether the models are installed.</param>
    /// <param name="recorder">While it is not idle the chat is paused.</param>
    /// <param name="scope">One session, or <c>null</c> for the whole library.</param>
    /// <param name="modelsDirectory">Shown in the install hint.</param>
    /// <param name="post">Marshals a callback to the UI thread; recorder events arrive on its own thread.</param>
    public ChatViewModel(IAssistant? assistant, ILlm? llm, IRecorder? recorder, SessionId? scope, string? modelsDirectory = null, Action<Action>? post = null)
    {
        _assistant = assistant;
        _llm = llm;
        _recorder = recorder;
        _modelsDirectory = modelsDirectory ?? string.Empty;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        Scope = scope;
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowEmptyHint));
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

    public SessionId? Scope { get; }

    /// <summary>The conversation, oldest first.</summary>
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    public partial string Question { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditQuestion))]
    public partial bool IsGenerating { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
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

        AskTask = RunAskAsync(Question.Trim());
    }

    private bool CanCancel() => IsGenerating;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _generation?.Cancel();

    private async Task RunAskAsync(string question)
    {
        var history = BuildHistory();
        var answer = ChatMessageViewModel.Answer();
        Messages.Add(ChatMessageViewModel.User(question));
        Messages.Add(answer);
        Question = string.Empty;
        var generation = new CancellationTokenSource();
        _generation = generation;
        IsGenerating = true;
        ScrollToEndRequested?.Invoke();
        try
        {
            var request = new AskRequest(question, Scope, history);
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
                    answer.SetCitations(update.Citations, OnCitation);
                    ScrollToEndRequested?.Invoke();
                }
            }

            if (generation.IsCancellationRequested)
            {
                answer.Stop(CancelledText);
            }
            else
            {
                answer.Complete();
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
