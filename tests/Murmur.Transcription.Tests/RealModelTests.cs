using Murmur.Core.Transcription.Protocol;
using Murmur.Transcription.Audio;
using Murmur.Transcription.Native;
using Murmur.Transcription.Pipeline;

namespace Murmur.Transcription.Tests;

/// <summary>sherpa-onnx + Parakeet + Silero in process (skipped when the models are not on disk).</summary>
[Collection(ModelCollection.Name)]
public sealed class RealModelTests(ModelFixture fixture)
{
    private static ConcatenatedSource Mixed(long seconds) =>
        new([Models.TestClip16k("en"), Models.TestClip16k("de"), Models.TestClip16k("es"), Models.TestClip16k("fr")], 16_000L * seconds);

    private List<WorkerSegment> Run(ISampleSource source, PipelineOptions options)
    {
        var segments = new List<WorkerSegment>();
        var pipeline = new TranscriptionPipeline(
            new NativeSpeechRecognizer(fixture.Recognizer),
            () => new SileroVad(VoiceActivityDetector.CreateSilero(Models.Vad, options.Vad)),
            options);
        pipeline.Run(source, segments.Add, null, CancellationToken.None);
        return segments;
    }

    [Fact]
    public void RecognizerResult_HasSaneTokenTimestampsAndDurations()
    {
        Models.SkipIfMissing();
        var clip = Models.TestClip16k("en");
        var results = new RecognitionResult[1];

        fixture.Recognizer.Decode([clip.AsMemory()], results);

        var r = results[0];
        Assert.Contains("country", r.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(r.Tokens.Length, r.Timestamps.Length);
        Assert.Equal(r.Tokens.Length, r.Durations.Length); // the NuGet binding returned garbage here (D-013)
        var length = clip.Length / 16_000f;
        for (var i = 0; i < r.Tokens.Length; i++)
        {
            Assert.InRange(r.Timestamps[i], 0f, length);
            Assert.InRange(r.Durations[i], 0f, 2f); // seconds (TDT frames are 80 ms)
            if (i > 0)
            {
                Assert.True(r.Timestamps[i] >= r.Timestamps[i - 1]);
            }
        }

        // sherpa-onnx hands out SentencePiece word starts as a leading space (not U+2581) for NeMo models.
        Assert.True(r.Tokens[0][0] is ' ' or WordBuilder.WordBoundary, $"first token '{r.Tokens[0]}'");
    }

    [Fact]
    public void Vad_SplitsMixedLanguages_SoEveryLanguageIsRecognised()
    {
        Models.SkipIfMissing();

        var segments = Run(Mixed(25), new PipelineOptions());

        var text = string.Join(' ', segments.Select(s => s.Text));
        Assert.True(segments.Count >= 5, $"{segments.Count} segments: {text}");
        Assert.Contains("country", text, StringComparison.OrdinalIgnoreCase); // en
        Assert.Contains("Wurst", text, StringComparison.Ordinal);               // de
        Assert.Contains("país", text, StringComparison.Ordinal);                // es
        Assert.Contains("pays", text, StringComparison.Ordinal);                // fr
        Assert.All(segments, s => Assert.True(s.End - s.Start <= 20.0));
        Assert.StartsWith("Ask not", segments[0].Text, StringComparison.Ordinal); // onset not clipped
    }

    [Fact]
    public void ParallelDecoding_GivesTheSameTranscriptAsSequential()
    {
        Models.SkipIfMissing();

        var sequential = Run(Mixed(40), new PipelineOptions { Parallelism = 1 });
        var parallel = Run(Mixed(40), new PipelineOptions { Parallelism = 4 });
        var batched = Run(Mixed(40), new PipelineOptions { Parallelism = 2, BatchSize = 3 });

        Assert.Equal(sequential.Select(s => (s.Start, s.End)), parallel.Select(s => (s.Start, s.End)));
        Assert.Equal(sequential.Select(s => s.Text), parallel.Select(s => s.Text));
        Assert.Equal(sequential.Select(s => (s.Start, s.End)), batched.Select(s => (s.Start, s.End)));
        Assert.Equal(sequential.Count, batched.Count(s => s.Text.Length > 0));
    }

    [Fact]
    public void NonAsciiPaths_WorkForModelsVadAndAudio()
    {
        Models.SkipIfMissing();
        using var dir = new TempDir("Владислав");
        var models = Path.Combine(dir.Path, "моделі parakeet");
        Directory.CreateDirectory(models);
        try
        {
            // Symlinks keep the 650 MB encoder off the disk; the native code still opens the Cyrillic path.
            foreach (var f in (string[])["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx"])
            {
                File.CreateSymbolicLink(Path.Combine(models, f), Path.Combine(Models.ParakeetDir, f));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("cannot create symbolic links here: " + ex.Message);
        }

        File.Copy(Path.Combine(Models.ParakeetDir, "tokens.txt"), Path.Combine(models, "tokens.txt"));
        var vad = Path.Combine(dir.Path, "силеро vad.onnx");
        File.Copy(Models.Vad, vad); // a real copy: Silero is opened through the UTF-8 path itself
        var wav = Path.Combine(dir.Path, "запис.wav");
        WavFile.Write(wav, Models.TestClip16k("en"), 16_000);

        using var engine = new SherpaTranscriptionEngine(new EngineOptions { ModelDirectory = models, VadModelPath = vad, Threads = 2 });
        var segments = new List<WorkerSegment>();
        engine.Transcribe(wav, segments.Add, null, CancellationToken.None);

        Assert.Contains("country", string.Join(' ', segments.Select(s => s.Text)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SileroVad_CreatesFromANonAsciiPath()
    {
        Models.SkipIfMissing();
        using var dir = new TempDir("вад");
        var vadPath = Path.Combine(dir.Path, "модель вад.onnx");
        File.Copy(Models.Vad, vadPath);

        using var vad = VoiceActivityDetector.CreateSilero(vadPath, new VadOptions());
        vad.AcceptWaveform(new float[512]);

        Assert.False(vad.TryDequeue(out _));
    }

    [Fact]
    public void MissingModelFiles_FailWithFileNotFound_BeforeNativeCode()
    {
        using var dir = new TempDir();

        var asr = Assert.Throws<FileNotFoundException>(() => OfflineRecognizer.CreateParakeet(new ParakeetModelFiles(dir.File("nothing")), 1));
        Assert.Contains("encoder", asr.Message, StringComparison.Ordinal);
        Assert.Throws<FileNotFoundException>(() => VoiceActivityDetector.CreateSilero(dir.File("missing.onnx"), new VadOptions()));
    }
}
