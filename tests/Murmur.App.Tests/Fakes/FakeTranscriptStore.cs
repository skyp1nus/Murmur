using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests.Fakes;

internal sealed class FakeTranscriptStore : ITranscriptStore
{
    public Transcript? Transcript { get; set; }

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
        return ReadFailure is { } failure ? throw failure : ValueTask.FromResult(Transcript);
    }
}
