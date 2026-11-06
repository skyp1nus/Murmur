using Murmur.Core.Sessions;

namespace Murmur.Core.Transcription;

/// <summary>Cheap diarization: the microphone track is "Me", the loopback track is "Others".</summary>
public enum Speaker
{
    Me,
    Others,
}

public readonly record struct TranscriptWord(string Text, TimeSpan Start, TimeSpan End)
{
    /// <summary>
    /// Below this confidence a word is shown as uncertain (D-052: on FLEURS en/uk two thirds of the words under it are real errors and it
    /// catches 38 % of them).
    /// </summary>
    public const float UncertainThreshold = 0.60f;

    /// <summary>
    /// ASR confidence 0..1 (D-052), or null when unknown (older transcripts, workers that do not report it).
    /// An init property rather than a positional parameter, so <c>new TranscriptWord(text, start, end)</c> stays valid.
    /// </summary>
    public float? Confidence { get; init; }

    /// <summary>The recogniser doubted the word (<see cref="Confidence"/> below <see cref="UncertainThreshold"/>); an unknown confidence is not uncertain.</summary>
    public bool IsUncertain => Confidence is < UncertainThreshold;
}

public sealed record TranscriptSegment(Speaker Speaker, TimeSpan Start, TimeSpan End, string Text, IReadOnlyList<TranscriptWord> Words);

/// <summary>Segments of all tracks, sorted by <see cref="TranscriptSegment.Start"/>.</summary>
public sealed record Transcript(SessionId Session, IReadOnlyList<TranscriptSegment> Segments, string Engine, string? Language = null);
