using Murmur.Core.Transcription.Protocol;
using Murmur.Transcription.Audio;

namespace Murmur.Transcription.Tests;

/// <summary>Protocol round trips against the real worker process. Tests that need no model run everywhere.</summary>
public sealed class WorkerProcessTests
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(30);

    [Fact]
    public void Serve_ReadyPingShutdown_Exits0_WithOnlyJsonOnStdout()
    {
        using var w = WorkerProcess.Serve();

        Assert.Equal(WorkerKinds.Ready, w.Next(Short).Kind);
        w.Send(new WorkerRequest { Kind = WorkerKinds.Ping, RequestId = "p" });
        var pong = w.Next(Short);
        Assert.Equal((WorkerKinds.Pong, "p"), (pong.Kind, pong.RequestId));
        w.Send(new WorkerRequest { Kind = WorkerKinds.Shutdown });

        Assert.Equal(0, w.WaitForExit(Short));
        Assert.All(w.RawLines, l => Assert.StartsWith("{\"kind\":", l, StringComparison.Ordinal));
        Assert.Contains("murmur-asr", w.Stderr, StringComparison.Ordinal); // diagnostics go to stderr
    }

    [Fact]
    public void Serve_MalformedLine_YieldsError_AndKeepsRunning()
    {
        using var w = WorkerProcess.Serve();
        w.Next(Short);

        w.SendRaw("this is not json");
        var error = w.Next(Short);
        Assert.Equal(WorkerKinds.Error, error.Kind);
        Assert.StartsWith("malformed request", error.Error, StringComparison.Ordinal);

        w.Send(new WorkerRequest { Kind = WorkerKinds.Ping });
        Assert.Equal(WorkerKinds.Pong, w.Next(Short).Kind);
        w.CloseStdin();
        Assert.Equal(0, w.WaitForExit(Short)); // stdin EOF = exit 0
    }

    [Fact]
    public void Serve_IdleTimeout_Exits0()
    {
        using var w = WorkerProcess.Serve("--idle-exit-seconds", "1");

        Assert.Equal(WorkerKinds.Ready, w.Next(Short).Kind);
        Assert.Equal(0, w.WaitForExit(Short));
        Assert.Contains("idle", w.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Serve_Non16kHzWav_IsRejectedBeforeAnyModelLoad()
    {
        using var dir = new TempDir();
        var wav = dir.File("cd-quality.wav");
        File.WriteAllBytes(wav, TestWav.Build(TestWav.Pcm, 44_100, 2, 16, new byte[44_100 * 4 / 10]));
        // A model directory that does not exist proves the WAV is validated first.
        using var w = WorkerProcess.Start("--serve", "--model-dir", dir.File("no-model"), "--vad", dir.File("no-vad.onnx"));
        w.Next(Short);

        w.Send(new WorkerRequest { Kind = WorkerKinds.Transcribe, RequestId = "r1", AudioPath = wav });
        var events = w.UntilTerminal("r1", Short);

        var error = Assert.Single(events);
        Assert.Equal(WorkerKinds.Error, error.Kind);
        Assert.Contains("44100 Hz, 2 ch", error.Error, StringComparison.Ordinal);
        Assert.Contains("16000 Hz mono", error.Error, StringComparison.Ordinal);

        w.Send(new WorkerRequest { Kind = WorkerKinds.Transcribe, RequestId = "r2", AudioPath = dir.File("missing.wav") });
        Assert.Contains("not found", w.UntilTerminal("r2", Short)[^1].Error, StringComparison.OrdinalIgnoreCase);

        w.Send(new WorkerRequest { Kind = WorkerKinds.Shutdown });
        Assert.Equal(0, w.WaitForExit(Short));
    }

    [Fact]
    public void Serve_MissingModel_IsAJobError_NotACrash()
    {
        using var dir = new TempDir();
        var wav = dir.File("ok.wav");
        WavFile.Write(wav, new float[16_000], 16_000);
        using var w = WorkerProcess.Start("--serve", "--model-dir", dir.File("no-model"), "--vad", dir.File("no-vad.onnx"));
        w.Next(Short);

        w.Send(new WorkerRequest { Kind = WorkerKinds.Transcribe, RequestId = "m", AudioPath = wav });
        var error = w.UntilTerminal("m", Short)[^1];

        Assert.Equal(WorkerKinds.Error, error.Kind);
        Assert.Contains("not found", error.Error, StringComparison.OrdinalIgnoreCase);
        w.Send(new WorkerRequest { Kind = WorkerKinds.Ping });
        Assert.Equal(WorkerKinds.Pong, w.Next(Short).Kind);
    }
}

/// <summary>Real-model protocol round trips (skipped when the models are not on disk).</summary>
[Collection(ModelCollection.Name)]
public sealed class WorkerProcessModelTests
{
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(3);

    [Fact]
    public void Serve_TranscribesEnglish_WithMonotonicWordTimesInsideTheAudio()
    {
        Models.SkipIfMissing();
        using var dir = new TempDir();
        var wav = dir.File("en16k.wav");
        var clip = Models.TestClip16k("en");
        WavFile.Write(wav, clip, 16_000);
        var duration = clip.Length / 16_000.0;
        using var w = WorkerProcess.Serve("--threads", "4");
        Assert.Equal(WorkerKinds.Ready, w.Next(Long).Kind);

        w.Send(new WorkerRequest { Kind = WorkerKinds.Transcribe, RequestId = "en", AudioPath = wav, Language = "en" });
        var events = w.UntilTerminal("en", Long);

        Assert.Equal(WorkerKinds.Done, events[^1].Kind);
        Assert.Equal(duration, events[^1].TotalSeconds, 3);
        Assert.Contains(events, e => e.Kind == WorkerKinds.Progress);
        var segments = events.Where(e => e.Kind == WorkerKinds.Segment).Select(e => e.Segment!).ToList();
        Assert.NotEmpty(segments);
        var text = string.Join(' ', segments.Select(s => s.Text));
        Assert.Contains("country", text, StringComparison.OrdinalIgnoreCase);

        var words = segments.SelectMany(s => s.Words).ToList();
        Assert.True(words.Count >= 10, $"only {words.Count} words: {text}");
        Assert.Contains(words, x => x.Text.StartsWith("country", StringComparison.OrdinalIgnoreCase));
        for (var i = 0; i < words.Count; i++)
        {
            Assert.InRange(words[i].Start, 0, duration);
            Assert.InRange(words[i].End, words[i].Start, duration);
            if (i > 0)
            {
                Assert.True(words[i].Start >= words[i - 1].Start, $"word {i} starts before word {i - 1}");
                Assert.True(words[i].Start >= words[i - 1].End - 1e-6, $"word {i} overlaps word {i - 1}");
            }
        }

        for (var i = 1; i < segments.Count; i++)
        {
            Assert.True(segments[i].Start >= segments[i - 1].Start, "segments out of time order");
        }

        w.Send(new WorkerRequest { Kind = WorkerKinds.Shutdown });
        Assert.Equal(0, w.WaitForExit(Long));
    }

    [Fact]
    public void Serve_CancelOfALongJob_StopsPromptlyWithErrorCancelled()
    {
        Models.SkipIfMissing();
        using var dir = new TempDir();
        var wav = dir.File("long.wav");
        using (var source = new ConcatenatedSource([Models.TestClip16k("en"), Models.TestClip16k("de")], 16_000L * 600))
        {
            WavFile.Write(wav, source); // 10 minutes
        }

        using var w = WorkerProcess.Serve("--threads", "2");
        w.Next(Long);
        w.Send(new WorkerRequest { Kind = WorkerKinds.Transcribe, RequestId = "long", AudioPath = wav });

        // Wait until decoding is under way (the model loads first), then cancel.
        WorkerEvent e;
        do
        {
            e = w.Next(Long);
            Assert.NotEqual(WorkerKinds.Done, e.Kind);
            Assert.NotEqual(WorkerKinds.Error, e.Kind);
        }
        while (e.Kind != WorkerKinds.Segment);

        var cancelledAt = DateTime.UtcNow;
        w.Send(new WorkerRequest { Kind = WorkerKinds.Cancel, RequestId = "long" });
        var rest = w.UntilTerminal("long", TimeSpan.FromSeconds(60));

        Assert.Equal(WorkerKinds.Error, rest[^1].Kind);
        Assert.Equal("cancelled", rest[^1].Error);
        Assert.DoesNotContain(rest, x => x.Kind == WorkerKinds.Done);
        Assert.True(DateTime.UtcNow - cancelledAt < TimeSpan.FromSeconds(30), "cancel was not prompt");
        var lastSegment = rest.LastOrDefault(x => x.Kind == WorkerKinds.Segment)?.Segment;
        Assert.True(lastSegment is null || lastSegment.End < 300, "most of the file was transcribed despite the cancel");

        w.Send(new WorkerRequest { Kind = WorkerKinds.Ping, RequestId = "alive" });
        Assert.Equal("alive", w.Next(Long).RequestId);
    }
}
