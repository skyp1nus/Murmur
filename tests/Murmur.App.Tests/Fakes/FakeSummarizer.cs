using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests.Fakes;

/// <summary>
/// Scriptable <see cref="ISessionSummarizer"/>: by default it answers at once with <see cref="Result"/>; with <see cref="Gate"/> set it
/// waits for it (progress and cancellation can be tested), with <see cref="Failure"/> it throws.
/// </summary>
internal sealed class FakeSummarizer : ISessionSummarizer
{
    public int Calls { get; private set; }

    public List<SessionId> Sessions { get; } = [];

    public Exception? Failure { get; set; }

    /// <summary>When set, the call reports <see cref="ProgressSteps"/> and waits until the test completes it.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public double[] ProgressSteps { get; set; } = [];

    public bool Cancelled { get; private set; }

    public SessionSummary? Result { get; set; }

    /// <summary>Runs just before the summary is returned (the user presses Stop right then).</summary>
    public Action? BeforeReturn { get; set; }

    public async ValueTask<SessionSummary> SummarizeAsync(SessionId session, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Calls++;
        Sessions.Add(session);
        foreach (var step in ProgressSteps)
        {
            progress?.Report(step);
        }

        if (Gate is { } gate)
        {
            try
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        BeforeReturn?.Invoke();
        return (Result ?? Sample(session)) with { Session = session };
    }

    /// <summary>The summary of the Session artboard of the canvas.</summary>
    public static SessionSummary Sample(SessionId session) => new(
        session,
        "Walked the client through the roadmap: recording and transcription are ready, library search is in progress. PDF export moved to next sprint; the demo is set for next week.",
        [new SummaryItem("PDF export — next sprint, after summaries and action items", TimeSpan.FromSeconds(772)), new SummaryItem("Demo — next week", TimeSpan.FromSeconds(795))],
        [new SummaryItem("You — send the demo invite", TimeSpan.FromSeconds(804)), new SummaryItem("Add PDF export to the sprint plan", TimeSpan.FromSeconds(772))],
        "gemma-4-E4B-it-Q4_K_M",
        new DateTimeOffset(2026, 10, 18, 15, 0, 0, TimeSpan.Zero));
}

/// <summary>An in-memory <see cref="ISummaryStore"/> that records what was saved and deleted.</summary>
internal sealed class FakeSummaryStore : ISummaryStore
{
    private readonly Dictionary<SessionId, SessionSummary> _summaries = [];

    public List<SessionSummary> Saved { get; } = [];

    public List<SessionId> Deleted { get; } = [];

    public Exception? SaveFailure { get; set; }

    public Exception? GetFailure { get; set; }

    public void Put(SessionSummary summary) => _summaries[summary.Session] = summary;

    public ValueTask<SessionSummary?> GetSummaryAsync(SessionId session, CancellationToken cancellationToken)
    {
        if (GetFailure is { } failure)
        {
            throw failure;
        }

        return ValueTask.FromResult(_summaries.GetValueOrDefault(session));
    }

    public ValueTask SaveSummaryAsync(SessionSummary summary, CancellationToken cancellationToken)
    {
        if (SaveFailure is { } failure)
        {
            throw failure;
        }

        _summaries[summary.Session] = summary;
        Saved.Add(summary);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteSummaryAsync(SessionId session, CancellationToken cancellationToken)
    {
        _summaries.Remove(session);
        Deleted.Add(session);
        return ValueTask.CompletedTask;
    }
}
