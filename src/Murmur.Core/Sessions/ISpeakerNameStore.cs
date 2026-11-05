using Murmur.Core.Transcription;

namespace Murmur.Core.Sessions;

/// <summary>User-chosen names of the two speakers of a session; null = the default label ("You" / "Others").</summary>
public sealed record SpeakerNames(string? Me, string? Others)
{
    public static readonly SpeakerNames Default = new(null, null);

    public string? this[Speaker speaker] => speaker == Speaker.Me ? Me : Others;
}

/// <summary>The stored speaker names of a session. They go with the session when it is deleted.</summary>
public interface ISpeakerNameStore
{
    /// <summary>Default when nothing was stored.</summary>
    ValueTask<SpeakerNames> GetSpeakerNamesAsync(SessionId session, CancellationToken cancellationToken);

    /// <summary>Stores a name (trimmed; empty or whitespace = back to the default). Unknown session throws KeyNotFoundException.</summary>
    ValueTask SetSpeakerNameAsync(SessionId session, Speaker speaker, string? name, CancellationToken cancellationToken);
}
