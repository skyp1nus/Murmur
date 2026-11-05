using System.Text;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Intelligence.Rag;

/// <summary>
/// Splits a transcript into retrieval chunks of up to <see cref="MaxDuration"/> along segment boundaries. Segments
/// are added greedily while the chunk stays within the limit, so chunks are usually 30–60 s long; a single segment
/// longer than the limit becomes a chunk of its own. Each line starts with the speaker label (<c>Я:</c> for the
/// microphone, <c>Інші:</c> for the system audio) or the name the user gave the speaker (<see cref="SpeakerNames"/>); consecutive segments of
/// one speaker share a line.
/// </summary>
public sealed class TranscriptChunker
{
    public const string MeLabel = "Я";
    public const string OthersLabel = "Інші";

    public TranscriptChunker(TimeSpan? maxDuration = null)
    {
        MaxDuration = maxDuration ?? TimeSpan.FromSeconds(60);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxDuration, TimeSpan.Zero, nameof(maxDuration));
    }

    /// <summary>Longest chunk, measured from the first segment's start to the last segment's end.</summary>
    public TimeSpan MaxDuration { get; }

    public static string Label(Speaker speaker) => speaker == Speaker.Me ? MeLabel : OthersLabel;

    /// <summary>The label of <paramref name="speaker"/> in a chunk: the user's name when <paramref name="names"/> has one, else the default.</summary>
    public static string Label(Speaker speaker, SpeakerNames? names) => names?[speaker] is { Length: > 0 } name ? name : Label(speaker);

    public IReadOnlyList<TextChunk> Chunk(Transcript transcript) => Chunk(transcript, null);

    /// <summary>
    /// Chunks the transcript; the lines start with the names in <paramref name="names"/> (<c>null</c> or an unset name = the default label).
    /// Chunks that were indexed before a rename keep the label they had in the index until the session is indexed again.
    /// </summary>
    public IReadOnlyList<TextChunk> Chunk(Transcript transcript, SpeakerNames? names) => Chunk(transcript, names, null);

    /// <summary>
    /// Like <see cref="Chunk(Transcript, SpeakerNames?)"/>, with <paramref name="fallbackLabel"/> (the UI word) for a speaker without a name
    /// instead of <c>Я</c> / <c>Інші</c>: for chunks that are shown, not indexed or sent to the model. <c>null</c> = the model labels.
    /// </summary>
    public IReadOnlyList<TextChunk> Chunk(Transcript transcript, SpeakerNames? names, Func<Speaker, string>? fallbackLabel)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        names = names.ForModel();
        var chunks = new List<TextChunk>();
        var current = new List<TranscriptSegment>();
        var start = TimeSpan.Zero;
        var end = TimeSpan.Zero;

        foreach (var segment in transcript.Segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            var segmentEnd = segment.End > segment.Start ? segment.End : segment.Start;
            if (current.Count > 0 && Max(end, segmentEnd) - start > MaxDuration)
            {
                chunks.Add(Build(transcript.Session, current, start, end, names, fallbackLabel));
                current.Clear();
            }

            if (current.Count == 0)
            {
                start = segment.Start;
                end = segmentEnd;
            }

            current.Add(segment);
            end = Max(end, segmentEnd);
        }

        if (current.Count > 0)
        {
            chunks.Add(Build(transcript.Session, current, start, end, names, fallbackLabel));
        }

        return chunks;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TextChunk Build(SessionId session, List<TranscriptSegment> segments, TimeSpan start, TimeSpan end, SpeakerNames? names, Func<Speaker, string>? fallbackLabel)
    {
        var text = new StringBuilder();
        Speaker? lineSpeaker = null;
        foreach (var segment in segments)
        {
            var words = segment.Text.Trim();
            if (lineSpeaker == segment.Speaker)
            {
                text.Append(' ').Append(words);
                continue;
            }

            if (lineSpeaker is not null)
            {
                text.Append('\n');
            }

            text.Append(names?[segment.Speaker] is { Length: > 0 } name ? name : fallbackLabel?.Invoke(segment.Speaker) ?? Label(segment.Speaker)).Append(": ").Append(words);
            lineSpeaker = segment.Speaker;
        }

        return new TextChunk(session, start, end, text.ToString());
    }
}
