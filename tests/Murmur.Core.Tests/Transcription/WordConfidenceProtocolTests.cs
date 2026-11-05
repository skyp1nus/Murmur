using System.Text.Json;
using Murmur.Core.Transcription;
using Murmur.Core.Transcription.Protocol;

namespace Murmur.Core.Tests.Transcription;

/// <summary>D-052: optional per-word confidence in the worker protocol and the transcript.</summary>
public sealed class WordConfidenceProtocolTests
{
    private static string Serialize(WorkerWord word) =>
        JsonSerializer.Serialize(
            new WorkerEvent { Kind = WorkerKinds.Segment, Segment = new WorkerSegment(0, 1, "hi", [word]) },
            WorkerJsonContext.Default.WorkerEvent);

    [Fact]
    public void Confidence_IsOmittedWhenNull_AndWrittenWhenKnown()
    {
        Assert.DoesNotContain("confidence", Serialize(new WorkerWord("hi", 0, 0.5)), StringComparison.Ordinal);
        Assert.Contains("\"confidence\":0.25", Serialize(new WorkerWord("hi", 0, 0.5, 0.25f)), StringComparison.Ordinal);
    }

    [Fact]
    public void OldAndNewWorkerLines_BothDeserialize()
    {
        const string Old = """{"kind":"segment","segment":{"start":0,"end":1,"text":"hi","words":[{"text":"hi","start":0,"end":0.5}]}}""";
        const string New = """{"kind":"segment","segment":{"start":0,"end":1,"text":"hi","words":[{"text":"hi","start":0,"end":0.5,"confidence":0.875}]}}""";

        var old = JsonSerializer.Deserialize(Old, WorkerJsonContext.Default.WorkerEvent)!.Segment!.Words[0];
        var @new = JsonSerializer.Deserialize(New, WorkerJsonContext.Default.WorkerEvent)!.Segment!.Words[0];

        Assert.Equal(new WorkerWord("hi", 0, 0.5), old);
        Assert.Null(old.Confidence);
        Assert.Equal(0.875f, @new.Confidence);
    }

    [Fact]
    public void TranscriptWord_ConfidenceDefaultsToUnknown_AndTakesPartInEquality()
    {
        var word = new TranscriptWord("hi", TimeSpan.Zero, TimeSpan.FromSeconds(1));

        Assert.Null(word.Confidence);
        Assert.Equal(word, new TranscriptWord("hi", TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        Assert.NotEqual(word, word with { Confidence = 0.5f });
        Assert.Equal(0.5f, (word with { Confidence = 0.5f }).Confidence);
    }
}
