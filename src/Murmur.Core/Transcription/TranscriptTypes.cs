using Murmur.Core.Sessions;

namespace Murmur.Core.Transcription;

/// <summary>Cheap diarization: the microphone track is "Me", the loopback track is "Others".</summary>
public enum Speaker
{
    Me,
    Others,
}

public readonly record struct TranscriptWord(string Text, TimeSpan Start, TimeSpan End);

public sealed record TranscriptSegment(Speaker Speaker, TimeSpan Start, TimeSpan End, string Text, IReadOnlyList<TranscriptWord> Words);

/// <summary>Segments of all tracks, sorted by <see cref="TranscriptSegment.Start"/>.</summary>
public sealed record Transcript(SessionId Session, IReadOnlyList<TranscriptSegment> Segments, string Engine, string? Language = null);
