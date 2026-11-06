using System.Text.Json;
using Murmur.Core.Transcription.Protocol;
using Murmur.Transcription.Native;
using Murmur.Transcription.Pipeline;

namespace Murmur.Transcription.Tests;

/// <summary>#58: <c>--serve</c> sends the confidence of every word (the Min aggregation of D-052) in the protocol.</summary>
public sealed class ServeConfidenceTests
{
    private static float Ln(double p) => (float)Math.Log(p);

    private static readonly float[] Samples = [.. Enumerable.Repeat(0f, 8_000), .. Enumerable.Repeat(0.1f, 16_000), .. Enumerable.Repeat(0f, 8_000)];

    private static List<WorkerSegment> Run(RecognitionResult result, PipelineOptions options)
    {
        var segments = new List<WorkerSegment>();
        new TranscriptionPipeline(new ScriptedRecognizer(result), () => new FakeVad(), options)
            .Run(new ConcatenatedLikeSource(Samples), segments.Add, null, CancellationToken.None);
        return segments;
    }

    private static RecognitionResult HiThere(bool withLogProbs = true) =>
        new("hi there", ["▁hi", "▁the", "re"], [0f, 0.3f, 0.5f], [0.2f, 0.2f, 0.2f], "en")
        {
            LogProbs = withLogProbs ? [Ln(0.95), Ln(0.7), Ln(0.2)] : [],
        };

    [Fact]
    public void WithConfidenceOn_EveryWordCarriesItsConfidence()
    {
        var segment = Assert.Single(Run(HiThere(), new PipelineOptions { IncludeConfidence = true }));

        Assert.Equal(["hi", "there"], segment.Words.Select(w => w.Text));
        Assert.Equal(0.95f, segment.Words[0].Confidence!.Value, 4);
        Assert.Equal(0.2f, segment.Words[1].Confidence!.Value, 4);
    }

    [Fact]
    public void ByDefault_AndWithoutLogProbs_TheWordsHaveNoConfidence()
    {
        Assert.False(new PipelineOptions().IncludeConfidence);
        Assert.All(Assert.Single(Run(HiThere(), new PipelineOptions())).Words, w => Assert.Null(w.Confidence));
        Assert.All(Assert.Single(Run(HiThere(withLogProbs: false), new PipelineOptions { IncludeConfidence = true })).Words, w => Assert.Null(w.Confidence));
    }

    [Fact]
    public void TheDiagnosticHook_SeesTheSameNumbers_AsTheWords()
    {
        float[]? hooked = null;
        var segment = Assert.Single(Run(HiThere(), new PipelineOptions { IncludeConfidence = true, OnWordConfidence = (_, c) => hooked = c }));

        Assert.Equal(segment.Words.Select(w => w.Confidence!.Value), hooked!);
    }

    [Fact]
    public void TheSegmentEvent_CarriesConfidenceInItsJson()
    {
        var segment = Assert.Single(Run(HiThere(), new PipelineOptions { IncludeConfidence = true }));
        var json = JsonSerializer.Serialize(new WorkerEvent { Kind = WorkerKinds.Segment, RequestId = "r", Segment = segment }, WorkerJsonContext.Default.WorkerEvent);

        using var document = JsonDocument.Parse(json);
        var words = document.RootElement.GetProperty("segment").GetProperty("words");
        Assert.Equal(0.95, words[0].GetProperty("confidence").GetDouble(), 3);
        Assert.Equal(0.2, words[1].GetProperty("confidence").GetDouble(), 3);
    }

    [Fact]
    public void Serve_TurnsConfidenceOn_TheOtherCommandsDoNot()
    {
        Assert.True(CliOptions.TryParse(["--serve", "--model-dir", "/m", "--vad", "/v"], out var options, out _));

        Assert.True(Program.ServeEngineOptions(options).Pipeline.IncludeConfidence);
        Assert.False(Program.EngineOptionsFrom(options).Pipeline.IncludeConfidence);
    }
}
