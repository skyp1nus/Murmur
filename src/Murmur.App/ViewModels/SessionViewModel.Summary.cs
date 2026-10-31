using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>What the Summary tab shows.</summary>
public enum SummaryStatus
{
    /// <summary>The stored summary is being read.</summary>
    Loading,

    /// <summary>There is none yet: the empty state with "Summarize".</summary>
    None,

    /// <summary>The model is writing it: progress and "Stop".</summary>
    Working,

    /// <summary>The overview, the key points and the action items.</summary>
    Ready,

    /// <summary>The last attempt failed: the reason and "Try again".</summary>
    Failed,
}

/// <summary>
/// The Summary tab: an overview, key points and action items made by the local model (<see cref="ISessionSummarizer"/>) and kept
/// in the store, so they are there at the next visit. The model never runs while a recording does (the same check as the chat:
/// <see cref="ChatViewModel.Availability"/>), and the work stops when the view closes.
/// </summary>
public sealed partial class SessionViewModel
{
    private CancellationTokenSource? _summaryRun;
    private SessionSummary? _stored;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSummaryEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowSummaryWorking))]
    [NotifyPropertyChangedFor(nameof(ShowSummaryReady))]
    [NotifyPropertyChangedFor(nameof(ShowSummaryFailed))]
    public partial SummaryStatus SummaryState { get; private set; } = SummaryStatus.Loading;

    /// <summary>The overview paragraph.</summary>
    [ObservableProperty]
    public partial string SummaryOverview { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyPoints))]
    public partial IReadOnlyList<SummaryItemViewModel> KeyPoints { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionItems))]
    public partial IReadOnlyList<SummaryItemViewModel> ActionItems { get; private set; } = [];

    /// <summary>"Generated locally · Gemma 4 E4B".</summary>
    [ObservableProperty]
    public partial string SummaryFooter { get; private set; } = string.Empty;

    /// <summary>Progress of the run in whole percent.</summary>
    [ObservableProperty]
    public partial double SummaryProgress { get; private set; }

    [ObservableProperty]
    public partial string SummaryProgressText { get; private set; } = string.Empty;

    /// <summary>Why the last attempt failed.</summary>
    [ObservableProperty]
    public partial string? SummaryError { get; private set; }

    /// <summary>Why a summary cannot be made right now (the models are missing, a recording runs, there is no transcript), or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummaryNotice))]
    public partial string? SummaryNotice { get; private set; }

    public bool HasSummaryNotice => SummaryNotice is not null;

    public bool HasKeyPoints => KeyPoints.Count > 0;

    public bool HasActionItems => ActionItems.Count > 0;

    public bool ShowSummaryEmpty => SummaryState is SummaryStatus.None or SummaryStatus.Loading;

    public bool ShowSummaryWorking => SummaryState == SummaryStatus.Working;

    public bool ShowSummaryReady => SummaryState == SummaryStatus.Ready;

    public bool ShowSummaryFailed => SummaryState == SummaryStatus.Failed;

    /// <summary>Why the Summary tab asks to install the models: no summarizer, or the models are not there.</summary>
    public bool SummaryNeedsModels => _services.Summarizer is null || Chat.Availability == ChatAvailability.NotInstalled;

    /// <summary>"Summarize" / "Regenerate" can start: the models are there, no recording runs and the transcript is on screen.</summary>
    public bool CanSummarize => SummaryAvailable() is null && SummaryState != SummaryStatus.Working;

    /// <summary>The running summary (for tests); completes when it finished, failed or was stopped.</summary>
    internal Task SummaryTask { get; private set; } = Task.CompletedTask;

    private async Task LoadSummaryAsync()
    {
        if (_disposed)
        {
            return;
        }

        SessionSummary? summary = null;
        if (_services.Summaries is { } store)
        {
            try
            {
                summary = await store.GetSummaryAsync(Id, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // A summary that cannot be read is as good as none: it can be made again.
            }
        }

        if (_disposed || SummaryState == SummaryStatus.Working)
        {
            return;
        }

        if (summary is null)
        {
            _stored = null;
            SummaryOverview = string.Empty;
            KeyPoints = [];
            ActionItems = [];
            SummaryState = SummaryStatus.None;
            RefreshSummaryAvailability();
            return;
        }

        DisplaySummary(summary);
    }

    /// <summary>Re-reads whether a summary can be made now (the models were installed, a recording started or ended).</summary>
    internal void RefreshSummaryAvailability()
    {
        SummaryNotice = SummaryAvailable();
        OnPropertyChanged(nameof(SummaryNeedsModels));
        OnPropertyChanged(nameof(CanSummarize));
        SummarizeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The reason a summary cannot be made now (words for the user), or <c>null</c> when it can.</summary>
    private string? SummaryAvailable()
    {
        if (_services.Summarizer is null || Chat.Availability == ChatAvailability.NotInstalled)
        {
            return Strings.Session_SummaryNotInstalled;
        }

        if (Chat.Availability == ChatAvailability.PausedForRecording)
        {
            return Strings.Session_SummaryPaused;
        }

        return Status == TranscriptStatus.Available ? null : Strings.Session_SummaryNoTranscript;
    }

    private void DisplaySummary(SessionSummary summary)
    {
        _stored = summary;
        SummaryOverview = summary.Overview;
        KeyPoints = [.. summary.KeyPoints.Select(item => new SummaryItemViewModel(item, PlayFrom))];
        ActionItems = [.. summary.ActionItems.Select(item => new SummaryItemViewModel(item, PlayFrom))];
        SummaryFooter = UiText.Format(Strings.Session_SummaryGenerated, ModelNames.Of(summary.ModelId));
        SummaryError = null;
        SummaryState = SummaryStatus.Ready;
        RefreshSummaryAvailability();
    }

    private bool CanStartSummary() => CanSummarize;

    /// <summary>"Summarize" and "Regenerate": the model writes the summary and it replaces the stored one.</summary>
    [RelayCommand(CanExecute = nameof(CanStartSummary))]
    private void Summarize()
    {
        RefreshSummaryAvailability();
        if (!CanSummarize || _services.Summarizer is not { } summarizer)
        {
            return;
        }

        CancelSummary();
        var run = new CancellationTokenSource();
        _summaryRun = run;
        SummaryTask = RunSummaryAsync(summarizer, run);
    }

    /// <summary>"Stop": the model stops and the tab goes back to what it showed before.</summary>
    [RelayCommand]
    private void CancelSummaryRun() => _summaryRun?.Cancel();

    private void CancelSummary()
    {
        var run = _summaryRun;
        _summaryRun = null;
        if (run is null)
        {
            return;
        }

        run.Cancel();
    }

    private async Task RunSummaryAsync(ISessionSummarizer summarizer, CancellationTokenSource run)
    {
        SummaryProgress = 0;
        SummaryProgressText = UiText.Format(Strings.Common_Percent, 0);
        SummaryState = SummaryStatus.Working;
        RefreshSummaryAvailability();
        var progress = new PostedProgress(value => _post(() => ShowSummaryProgress(run, value)));
        try
        {
            // The model works on the thread pool: the first request starts llama-server and reads the transcript.
            var summary = await Task.Run(async () => await summarizer.SummarizeAsync(Id, progress, run.Token).ConfigureAwait(false), run.Token).ConfigureAwait(true);
            if (_disposed || run.IsCancellationRequested)
            {
                // "Stop" came while the result was on its way: the user asked to stop, so what was shown before comes back.
                if (!_disposed)
                {
                    RestoreAfterStop();
                }

                return;
            }

            if (_services.Summaries is { } store)
            {
                try
                {
                    await store.SaveSummaryAsync(summary, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // Not kept for the next visit, but it is shown now.
                }
            }

            DisplaySummary(summary);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            if (!_disposed)
            {
                RestoreAfterStop();
            }
        }
        catch (Exception ex)
        {
            if (_disposed)
            {
                return;
            }

            // The usual causes: a recording started (the models were stopped under the request) or the files vanished. The reason
            // (English, from the model server) goes to the log; the user gets a sentence in the UI language.
            Console.Error.WriteLine($"Summary of session {Id} failed: {ex.Message}");
            Chat.RefreshAvailability();
            SummaryError = Chat.Availability switch
            {
                ChatAvailability.PausedForRecording => Strings.Session_SummaryPaused,
                ChatAvailability.NotInstalled => Strings.Session_SummaryNotInstalled,
                _ => Strings.Session_SummaryFailed,
            };
            SummaryState = SummaryStatus.Failed;
            RefreshSummaryAvailability();
        }
        finally
        {
            if (ReferenceEquals(_summaryRun, run))
            {
                _summaryRun = null;
            }

            run.Dispose();
        }
    }

    private void RestoreAfterStop()
    {
        if (_stored is { } stored)
        {
            DisplaySummary(stored);
        }
        else
        {
            SummaryState = SummaryStatus.None;
            RefreshSummaryAvailability();
        }
    }

    private void ShowSummaryProgress(CancellationTokenSource run, double value)
    {
        if (_disposed || !ReferenceEquals(_summaryRun, run) || SummaryState != SummaryStatus.Working)
        {
            return;
        }

        SummaryProgress = Math.Clamp(value, 0, 1) * 100;
        SummaryProgressText = ProcessingText.PercentText(value);
    }

    /// <summary>An <see cref="IProgress{T}"/> that calls back at once on the reporting thread (no synchronization context to capture).</summary>
    private sealed class PostedProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

/// <summary>The model's name in "Generated locally · Gemma 4 E4B": the GGUF id the chat client reports, as the product calls it.</summary>
internal static class ModelNames
{
    public static string Of(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return string.Empty;
        }

        var id = modelId.Trim();
        var gemma = id.IndexOf("gemma-4-", StringComparison.OrdinalIgnoreCase);
        if (gemma < 0)
        {
            return id;
        }

        // "gemma-4-E4B-it-Q4_K_M", "google_gemma-4-26B-A4B-it-Q4_K_M": Gemma 4, then the size words up to "-it".
        var rest = id[(gemma + "gemma-4-".Length)..];
        var parts = new List<string>();
        foreach (var part in rest.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("it", StringComparison.OrdinalIgnoreCase) || part.StartsWith('Q') || part.StartsWith('q'))
            {
                break;
            }

            parts.Add(part.ToUpperInvariant());
        }

        return parts.Count == 0 ? "Gemma 4" : "Gemma 4 " + string.Join(' ', parts);
    }
}
