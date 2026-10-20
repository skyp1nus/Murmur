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
}
