using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests.Fakes;

internal sealed class FakeTranscriptStore : ITranscriptStore
{
    public Transcript? Transcript { get; set; }

    /// <summary>Transcripts by session; a session in here is answered with its own, any other with <see cref="Transcript"/>.</summary>
    public Dictionary<SessionId, Transcript> PerSession { get; } = [];

    public Exception? ReadFailure { get; set; }

    public int Reads { get; private set; }

    /// <summary>Session ids of every <see cref="GetTranscriptSummariesAsync"/> call, in order.</summary>
    public List<IReadOnlyList<SessionId>> SummaryRequests { get; } = [];

    public Exception? SummaryFailure { get; set; }

    public ValueTask SaveTranscriptAsync(Transcript transcript, CancellationToken cancellationToken)
    {
        Transcript = transcript;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Transcript?> GetTranscriptAsync(SessionId id, CancellationToken cancellationToken)
    {
        Reads++;
        if (ReadFailure is { } failure)
        {
            throw failure;
        }

        return ValueTask.FromResult(PerSession.TryGetValue(id, out var own) ? own : Transcript);
    }

    public ValueTask<IReadOnlyList<TranscriptSummary>> GetTranscriptSummariesAsync(IReadOnlyList<SessionId> sessions, CancellationToken cancellationToken)
    {
        lock (SummaryRequests)
        {
            SummaryRequests.Add(sessions);
        }

        if (SummaryFailure is { } failure)
        {
            throw failure;
        }

        var result = new List<TranscriptSummary>();
        foreach (var id in sessions)
        {
            var transcript = PerSession.TryGetValue(id, out var own) ? own : Transcript;
            if (transcript is not null)
            {
                result.Add(new TranscriptSummary(id, transcript.Language, transcript.Segments.Count > 0 ? transcript.Segments[0].Text : null));
            }
        }

        return ValueTask.FromResult<IReadOnlyList<TranscriptSummary>>(result);
    }
}
