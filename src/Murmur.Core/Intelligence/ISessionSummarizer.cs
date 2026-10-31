using Murmur.Core.Sessions;

namespace Murmur.Core.Intelligence;

/// <summary>
/// Writes the summary of a transcribed recording with the local chat model (<see cref="ILlm"/>). A long meeting is split into
/// chunks that are summarised one by one (map) and then merged (reduce); the times the model cites are resolved to real
/// transcript times. The summary is in the language of the transcript (English or Ukrainian). Like every use of the models it never
/// runs while a recording is in progress: the call fails then, and the model loads lazily in the worker and unloads as usual.
/// </summary>
public interface ISessionSummarizer
{
    /// <summary>
    /// Summarises the stored transcript of <paramref name="session"/>. Does not store the result (see <see cref="ISummaryStore"/>).
    /// Throws <see cref="InvalidOperationException"/> when the session has no transcript or the transcript has no speech,
    /// <see cref="InvalidDataException"/> when the model answered with nothing usable and <see cref="OperationCanceledException"/>
    /// when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="progress">Receives the share done in [0, 1], never going back.</param>
    ValueTask<SessionSummary> SummarizeAsync(SessionId session, IProgress<double>? progress, CancellationToken cancellationToken);
}
