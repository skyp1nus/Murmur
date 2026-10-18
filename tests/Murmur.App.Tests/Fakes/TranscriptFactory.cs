using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests.Fakes;

/// <summary>Deterministic fake transcripts.</summary>
internal static class TranscriptFactory
{
    private static readonly string[] Vocabulary =
    [
        "добре", "давайте", "почнемо", "з", "огляду", "спринту", "ми", "закрили", "більшість", "завдань",
        "але", "інтеграція", "з", "платіжним", "сервісом", "ще", "в", "роботі", "потрібно", "узгодити",
        "дедлайн", "та", "розподіл", "задач", "між", "командами", "так", "згоден", "я", "підготую",
    ];

    /// <summary>
    /// <paramref name="totalWords"/> words, <paramref name="wordsPerSegment"/> per segment, 0.3 s per word with a
    /// 0.8 s pause between segments; speakers alternate (Me, Others). 10 000 words last about 1 hour.
    /// </summary>
    public static Transcript Make(SessionId id, int totalWords, int wordsPerSegment = 12)
    {
        var segments = new List<TranscriptSegment>((totalWords / wordsPerSegment) + 1);
        var clock = TimeSpan.Zero;
        var spoken = 0;
        var index = 0;
        while (spoken < totalWords)
        {
            var count = Math.Min(wordsPerSegment, totalWords - spoken);
            var words = new TranscriptWord[count];
            var start = clock;
            for (var i = 0; i < count; i++)
            {
                var text = Vocabulary[(spoken + i) % Vocabulary.Length];
                words[i] = new TranscriptWord(text, clock, clock + TimeSpan.FromMilliseconds(250));
                clock += TimeSpan.FromMilliseconds(300);
            }

            segments.Add(new TranscriptSegment(
                index % 2 == 0 ? Speaker.Me : Speaker.Others,
                start,
                clock,
                string.Join(' ', words.Select(w => w.Text)),
                words));
            clock += TimeSpan.FromMilliseconds(800);
            spoken += count;
            index++;
        }

        return new Transcript(id, segments, "fake");
    }

    /// <summary>A segment whose words last 0.4 s each and start every 0.5 s from <paramref name="startSeconds"/>.</summary>
    public static TranscriptSegment Segment(Speaker speaker, double startSeconds, params string[] words)
    {
        var list = new TranscriptWord[words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            var start = TimeSpan.FromSeconds(startSeconds + (i * 0.5));
            list[i] = new TranscriptWord(words[i], start, start + TimeSpan.FromSeconds(0.4));
        }

        return new TranscriptSegment(
            speaker,
            TimeSpan.FromSeconds(startSeconds),
            TimeSpan.FromSeconds(startSeconds + (words.Length * 0.5)),
            string.Join(' ', words),
            list);
    }
}
