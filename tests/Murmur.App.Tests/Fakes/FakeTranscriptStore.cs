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

    /// <summary>Every <see cref="ReplaceWordAsync"/> call, in order.</summary>
    public List<(SessionId Session, int Segment, int Word, string Text)> Replacements { get; } = [];

    public Exception? ReplaceFailure { get; set; }

    /// <summary>Like the SQLite store: the word gets the text and an unknown confidence, the segment text is rebuilt from the words.</summary>
    public ValueTask ReplaceWordAsync(SessionId session, int segmentIndex, int wordIndex, string text, CancellationToken cancellationToken)
    {
        if (ReplaceFailure is { } failure)
        {
            throw failure;
        }

        var transcript = PerSession.TryGetValue(session, out var own) ? own : Transcript;
        if (transcript is null || segmentIndex < 0 || segmentIndex >= transcript.Segments.Count
            || wordIndex < 0 || wordIndex >= transcript.Segments[segmentIndex].Words.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        }

        Replacements.Add((session, segmentIndex, wordIndex, text));
        var segments = transcript.Segments.ToArray();
        var segment = segments[segmentIndex];
        var words = segment.Words.ToArray();
        words[wordIndex] = words[wordIndex] with { Text = text.Trim(), Confidence = null };
        segments[segmentIndex] = segment with { Words = words, Text = string.Join(' ', words.Select(w => w.Text)) };
        var updated = transcript with { Segments = segments };
        if (PerSession.ContainsKey(session))
        {
            PerSession[session] = updated;
        }
        else
        {
            Transcript = updated;
        }

        return ValueTask.CompletedTask;
    }
}
