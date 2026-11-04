using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Murmur.Transcription.Audio;
using Murmur.Transcription.Native;
using Murmur.Transcription.Pipeline;

namespace Murmur.Transcription.Bench;

internal sealed record ConfidenceEvalOptions
{
    public required string ManifestPath { get; init; }

    /// <summary>Clips per language decoded again with beam search (beam is several times slower than greedy).</summary>
    public int BeamClips { get; init; } = 20;

    public int MaxActivePaths { get; init; } = 4;

    /// <summary>SentencePiece vocabulary for the hotwords run; without it the hotwords run is skipped.</summary>
    public string? BpeVocab { get; init; }

    public float HotwordsScore { get; init; } = 1.5f;

    /// <summary>Directory for the generated hotwords file (default: the system temp directory).</summary>
    public string? WorkDirectory { get; init; }
}

internal sealed record WordReport(string Text, string Normalized, WordLabel Label, string[] Tokens, float[] LogProbs, float Min, float Mean, float GeometricMean);

internal sealed record ClipReport
{
    public required string Language { get; init; }

    public required string Wav { get; init; }

    public required string Reference { get; init; }

    public required string Hypothesis { get; init; }

    public double AudioSeconds { get; init; }

    public double DecodeMs { get; init; }

    public int ReferenceWords { get; init; }

    public int Substitutions { get; init; }

    public int Insertions { get; init; }

    public int Deletions { get; init; }

    /// <summary>Reference words the recogniser missed (substituted or deleted), as written in the reference.</summary>
    public IReadOnlyList<string> Missed { get; init; } = [];

    public IReadOnlyList<WordReport> Words { get; init; } = [];
}

internal sealed record SummaryReport(string Aggregation, string Language, ScoreSummary Summary);

internal sealed record DecodingRun
{
    public required string Name { get; init; }

    public required string Method { get; init; }

    public int MaxActivePaths { get; init; }

    public int Hotwords { get; init; }

    public int Clips { get; init; }

    public double AudioSeconds { get; init; }

    public double LoadMs { get; init; }

    public double DecodeMs { get; init; }

    public double Rtf { get; init; }

    public int ReferenceWords { get; init; }

    public int Errors { get; init; }

    public double Wer { get; init; }

    /// <summary>Word edits needed to turn the greedy transcript of the same clips into this one.</summary>
    public int ChangedWordsVsGreedy { get; init; }

    public double PeakRssMb { get; init; }

    /// <summary>Min-aggregated confidence of this run's words (beam log-probs include hotword boosts).</summary>
    public ScoreSummary? Confidence { get; init; }
}

internal sealed record EvalReport
{
    public string Native { get; init; } = string.Empty;

    public string Runtime { get; init; } = string.Empty;

    public required MachineInfo Machine { get; init; }

    public string Manifest { get; init; } = string.Empty;

    public int Threads { get; init; }

    public IReadOnlyList<SummaryReport> Summaries { get; init; } = [];

    public IReadOnlyList<DecodingRun> Decoding { get; init; } = [];

    public IReadOnlyList<string> HotwordsUsed { get; init; } = [];

    public IReadOnlyList<ClipReport> Clips { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(EvalReport))]
internal sealed partial class EvalJsonContext : JsonSerializerContext;

/// <summary>
/// <c>--confidence-eval</c> (spike #53): decodes clips with known transcripts, labels every recognised word as correct
/// or wrong (substitution / insertion after normalisation) and reports how well the word confidence separates the
/// two, plus the cost and the effect of modified beam search and hotwords. Each clip is decoded as one utterance
/// (no VAD): the clips are short, and the scores are per token either way.
/// </summary>
internal static class ConfidenceEval
{
    private static readonly ConfidenceAggregation[] Aggregations = [ConfidenceAggregation.Min, ConfidenceAggregation.Mean, ConfidenceAggregation.GeometricMean];

    /// <param name="createRecognizer">Builds a recognizer for the given decoder settings (disposed after use when IDisposable).</param>
    /// <param name="threads">Reported only.</param>
    public static EvalReport Run(ConfidenceEvalOptions options, Func<RecognizerOptions, ISpeechRecognizer> createRecognizer, int threads, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(createRecognizer);
        ArgumentNullException.ThrowIfNull(log);
        var manifest = Path.GetFullPath(options.ManifestPath);
        var clips = EvalManifest.Parse(File.ReadAllLines(manifest), Path.GetDirectoryName(manifest) ?? ".");
        if (clips.Count == 0)
        {
            throw new InvalidDataException("The manifest has no clips: " + manifest);
        }

        var audio = new float[clips.Count][];
        for (var i = 0; i < clips.Count; i++)
        {
            audio[i] = WavFile.ReadAs16kMono(clips[i].WavPath);
        }

        log.WriteLine($"confidence-eval: {clips.Count} clip(s), {audio.Sum(a => a.Length) / 16_000.0:0.0} s audio");

        // 1. Greedy (the production decoder): confidence distribution and threshold quality.
        var greedy = Decode("greedy", RecognizerOptions.Default, clips, audio, Enumerable.Range(0, clips.Count).ToArray(), createRecognizer, log);
        var reports = new ClipReport[clips.Count];
        for (var i = 0; i < clips.Count; i++)
        {
            reports[i] = Score(clips[i], audio[i].Length, greedy.Results[i]!, greedy.Milliseconds[i]);
        }

        var summaries = new List<SummaryReport>();
        var languages = clips.Select(c => c.Language).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        foreach (var aggregation in Aggregations)
        {
            summaries.Add(new SummaryReport(aggregation.ToString(), "all", ConfidenceMetrics.Summarize(Scored(reports, aggregation))));
            foreach (var language in languages)
            {
                summaries.Add(new SummaryReport(aggregation.ToString(), language, ConfidenceMetrics.Summarize(Scored(reports.Where(r => r.Language == language), aggregation))));
            }
        }

        // 2. Beam search and hotwords on a subset (first N clips of every language).
        var subset = languages
            .SelectMany(l => Enumerable.Range(0, clips.Count).Where(i => clips[i].Language == l).Take(Math.Max(0, options.BeamClips)))
            .Order()
            .ToArray();
        var runs = new List<DecodingRun> { Summary(greedy, "greedy", RecognizerOptions.Default, 0, subset, clips, audio, reports, null) };
        var hotwords = new List<string>();
        if (subset.Length > 0)
        {
            var beamOptions = new RecognizerOptions { DecodingMethod = RecognizerOptions.ModifiedBeamSearch, MaxActivePaths = options.MaxActivePaths };
            var beam = Decode("beam", beamOptions, clips, audio, subset, createRecognizer, log);
            runs.Add(Summary(beam, $"beam {options.MaxActivePaths}", beamOptions, 0, subset, clips, audio, reports, beam.Results));

            if (!string.IsNullOrEmpty(options.BpeVocab))
            {
                // Oracle custom vocabulary: the reference words greedy search got wrong on these clips. An upper bound
                // for a user's own vocabulary list; the run also shows what the boost does to the other words.
                hotwords = subset.SelectMany(i => reports[i].Missed).Where(w => w.Length > 1).Distinct(StringComparer.Ordinal).ToList();
                if (hotwords.Count > 0)
                {
                    var dir = options.WorkDirectory ?? Path.GetTempPath();
                    Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, "murmur-hotwords.txt");
                    File.WriteAllLines(file, hotwords, new UTF8Encoding(false));
                    var hotOptions = beamOptions with { HotwordsFile = file, HotwordsScore = options.HotwordsScore, BpeVocab = options.BpeVocab };
                    var hot = Decode("beam+hotwords", hotOptions, clips, audio, subset, createRecognizer, log);
                    runs.Add(Summary(hot, $"beam {options.MaxActivePaths} + {hotwords.Count} hotwords (score {options.HotwordsScore.ToString(CultureInfo.InvariantCulture)})", hotOptions, hotwords.Count, subset, clips, audio, reports, hot.Results));
                }
            }
            else
            {
                log.WriteLine("confidence-eval: no --bpe-vocab, hotwords run skipped");
            }
        }

        return new EvalReport
        {
            Native = SafeNativeVersion(),
            Runtime = RuntimeInformation.FrameworkDescription + (RuntimeFeatureInfo.IsNativeAot ? " (Native AOT)" : " (JIT)"),
            Machine = BenchRunner.Machine(),
            Manifest = Path.GetFileName(manifest),
            Threads = threads,
            Summaries = summaries,
            Decoding = runs,
            HotwordsUsed = hotwords,
            Clips = reports,
        };
    }

    public static string ToJson(EvalReport report) => JsonSerializer.Serialize(report, EvalJsonContext.Default.EvalReport);

    /// <summary>Markdown digest of the report (tables for docs/REPORTS).</summary>
    public static string ToMarkdown(EvalReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(c, $"# Word confidence (spike #53)\n\n{report.Native}; {report.Runtime}; {report.Machine.Cpu} x{report.Machine.Cores}; threads {report.Threads}\n\n");
        foreach (var language in report.Clips.Select(x => x.Language).Distinct(StringComparer.Ordinal))
        {
            var cl = report.Clips.Where(x => x.Language == language).ToArray();
            var refs = cl.Sum(x => x.ReferenceWords);
            var errs = cl.Sum(x => x.Substitutions + x.Insertions + x.Deletions);
            sb.Append(c, $"- {language}: {cl.Length} clips, {cl.Sum(x => x.AudioSeconds):0.0} s, {refs} reference words, WER {Pct(errs, refs)} (S {cl.Sum(x => x.Substitutions)}, I {cl.Sum(x => x.Insertions)}, D {cl.Sum(x => x.Deletions)})\n");
        }

        sb.Append("\n## Separation of wrong words (greedy)\n\n| aggregation | lang | words | wrong | AUC | mean ok / wrong | median ok / wrong | best F1 at t | P | R |\n|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var s in report.Summaries)
        {
            var m = s.Summary;
            var b = m.BestF1;
            sb.Append(c, $"| {s.Aggregation} | {s.Language} | {m.Words} | {m.Wrong} | {m.Auc:0.000} | {m.MeanCorrect:0.000} / {m.MeanWrong:0.000} | {m.MedianCorrect:0.000} / {m.MedianWrong:0.000} | {(b is null ? "-" : b.Threshold.ToString("0.00", c))} | {(b is null ? "-" : b.Precision.ToString("0.00", c))} | {(b is null ? "-" : b.Recall.ToString("0.00", c))} |\n");
        }

        foreach (var s in report.Summaries.Where(x => x.Aggregation == WordConfidence.DefaultAggregation.ToString()))
        {
            sb.Append(c, $"\n### Thresholds, {s.Aggregation}, {s.Language} (flag when confidence < t)\n\n| t | flagged | true wrong | precision | recall | F1 |\n|---|---|---|---|---|---|\n");
            foreach (var p in s.Summary.Thresholds)
            {
                sb.Append(c, $"| {p.Threshold:0.00} | {p.Flagged} | {p.TruePositives} | {p.Precision:0.00} | {p.Recall:0.00} | {p.F1:0.00} |\n");
            }

            sb.Append(c, $"\nHistogram ({s.Language}): ");
            sb.AppendJoin(", ", s.Summary.Histogram.Select(h => string.Create(c, $"[{h.From:0.0},{h.To:0.0}) ok {h.Correct} / wrong {h.Wrong}")));
            sb.Append('\n');
        }

        sb.Append("\n## Decoding cost (same clips)\n\n| run | clips | audio s | load ms | decode ms | RTF | WER | changed vs greedy | peak RSS MB | AUC (min) |\n|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var r in report.Decoding)
        {
            sb.Append(c, $"| {r.Name} | {r.Clips} | {r.AudioSeconds:0.0} | {r.LoadMs:0} | {r.DecodeMs:0} | {r.Rtf:0.0000} | {r.Wer:0.0%} | {r.ChangedWordsVsGreedy} | {r.PeakRssMb:0} | {(r.Confidence is null ? "-" : r.Confidence.Auc.ToString("0.000", c))} |\n");
        }

        if (report.HotwordsUsed.Count > 0)
        {
            sb.Append(c, $"\nHotwords ({report.HotwordsUsed.Count}): {string.Join(", ", report.HotwordsUsed.Take(60))}\n");
        }

        sb.Append("\n## Wrong words (greedy), lowest confidence first\n\n| lang | word | label | min | geo | token probs |\n|---|---|---|---|---|---|\n");
        foreach (var (lang, w) in report.Clips.SelectMany(x => x.Words.Where(w => w.Label != WordLabel.Correct).Select(w => (x.Language, w))).OrderBy(x => x.w.Min).Take(80))
        {
            sb.Append(c, $"| {lang} | {Escape(w.Text)} | {w.Label} | {w.Min:0.000} | {w.GeometricMean:0.000} | {string.Join(' ', w.LogProbs.Select(p => Math.Exp(p).ToString("0.00", c)))} |\n");
        }

        sb.Append("\n## Correct words with the lowest confidence (false alarms)\n\n| lang | word | min | geo |\n|---|---|---|---|\n");
        foreach (var (lang, w) in report.Clips.SelectMany(x => x.Words.Where(w => w.Label == WordLabel.Correct).Select(w => (x.Language, w))).OrderBy(x => x.w.Min).Take(30))
        {
            sb.Append(c, $"| {lang} | {Escape(w.Text)} | {w.Min:0.000} | {w.GeometricMean:0.000} |\n");
        }

        return sb.ToString();
    }

    /// <summary>Scores one decoded clip: words with token log-probs, labels and confidences.</summary>
    internal static ClipReport Score(EvalClip clip, int samples, RecognitionResult result, double decodeMs)
    {
        var spans = WordBuilder.Spans(result.Tokens);
        var haveProbs = result.LogProbs.Length == result.Tokens.Length;
        var words = new List<WordReport>();
        var hypothesis = new List<string>();
        foreach (var (first, last, text) in spans)
        {
            var normalized = WordAlignment.Normalize(text);
            if (normalized.Length == 0)
            {
                continue; // punctuation-only word: neither scored nor aligned
            }

            var tokens = result.Tokens[first..(last + 1)];
            var probs = haveProbs ? result.LogProbs[first..(last + 1)] : Unknown(tokens.Length);
            words.Add(new WordReport(
                text,
                normalized,
                WordLabel.Correct,
                tokens,
                probs,
                WordConfidence.ForWord(tokens, probs, ConfidenceAggregation.Min),
                WordConfidence.ForWord(tokens, probs, ConfidenceAggregation.Mean),
                WordConfidence.ForWord(tokens, probs, ConfidenceAggregation.GeometricMean)));
            hypothesis.Add(normalized);
        }

        var (refNormalized, refRaw) = ReferenceWords(clip.Reference);
        var alignment = WordAlignment.Align(hypothesis, refNormalized);
        for (var i = 0; i < words.Count; i++)
        {
            words[i] = words[i] with { Label = alignment.Labels[i] };
        }

        return new ClipReport
        {
            Language = clip.Language,
            Wav = Path.GetFileName(clip.WavPath),
            Reference = clip.Reference,
            Hypothesis = result.Text,
            AudioSeconds = Math.Round(samples / 16_000.0, 3),
            DecodeMs = Math.Round(decodeMs, 1),
            ReferenceWords = alignment.ReferenceWords,
            Substitutions = alignment.Substitutions,
            Insertions = alignment.Insertions,
            Deletions = alignment.Deletions,
            Missed = [.. alignment.MissedReference.Select(i => refRaw[i])],
            Words = words,
        };
    }

    /// <summary>Normalised reference words and, index for index, the same words as written (punctuation trimmed).</summary>
    internal static (List<string> Normalized, List<string> Raw) ReferenceWords(string reference)
    {
        var normalized = new List<string>();
        var raw = new List<string>();
        foreach (var w in reference.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var n = WordAlignment.Normalize(w);
            if (n.Length > 0)
            {
                normalized.Add(n);
                raw.Add(w.Trim().Trim(TrimmedPunctuation));
            }
        }

        return (normalized, raw);
    }

    private static readonly char[] TrimmedPunctuation = ['.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '«', '»', '“', '”', '„', '—', '–', '…'];

    /// <summary>NaN log-probabilities: <see cref="WordConfidence.ForWord"/> scores such a word 0 (unknown).</summary>
    private static float[] Unknown(int count)
    {
        var a = new float[count];
        Array.Fill(a, float.NaN);
        return a;
    }

    private static List<ScoredWord> Scored(IEnumerable<ClipReport> clips, ConfidenceAggregation aggregation) =>
        [.. clips.SelectMany(c => c.Words).Select(w => new ScoredWord(
            aggregation switch
            {
                ConfidenceAggregation.Mean => w.Mean,
                ConfidenceAggregation.GeometricMean => w.GeometricMean,
                _ => w.Min,
            },
            w.Label != WordLabel.Correct))];

    private sealed record Decoded(RecognitionResult?[] Results, double[] Milliseconds, double LoadMs, double PeakRssMb);

    private static Decoded Decode(string name, RecognizerOptions decoding, List<EvalClip> clips, float[][] audio, int[] indices, Func<RecognizerOptions, ISpeechRecognizer> create, TextWriter log)
    {
        var results = new RecognitionResult?[clips.Count];
        var ms = new double[clips.Count];
        var sw = Stopwatch.StartNew();
        var recognizer = create(decoding);
        var loadMs = sw.Elapsed.TotalMilliseconds;
        try
        {
            var one = new RecognitionResult[1];
            var total = 0.0;
            foreach (var i in indices)
            {
                sw.Restart();
                recognizer.Decode([audio[i].AsMemory()], one);
                ms[i] = sw.Elapsed.TotalMilliseconds;
                total += ms[i];
                results[i] = one[0];
            }

            log.WriteLine($"confidence-eval: {name}: {indices.Length} clip(s) in {total:0} ms (load {loadMs:0} ms)");
        }
        finally
        {
            (recognizer as IDisposable)?.Dispose();
        }

        return new Decoded(results, ms, loadMs, Math.Round(BenchRunner.PeakRssBytes() / (1024.0 * 1024.0), 1));
    }

    private static DecodingRun Summary(Decoded run, string name, RecognizerOptions decoding, int hotwords, int[] subset, List<EvalClip> clips, float[][] audio, ClipReport[] greedy, RecognitionResult?[]? own)
    {
        int refs = 0, errors = 0, changed = 0;
        double audioSeconds = 0, decodeMs = 0;
        var scored = new List<ScoredWord>();
        foreach (var i in subset)
        {
            var report = own is null ? greedy[i] : Score(clips[i], audio[i].Length, own[i]!, run.Milliseconds[i]);
            refs += report.ReferenceWords;
            errors += report.Substitutions + report.Insertions + report.Deletions;
            audioSeconds += audio[i].Length / 16_000.0;
            decodeMs += run.Milliseconds[i];
            if (own is not null)
            {
                var diff = WordAlignment.Align([.. report.Words.Select(w => w.Normalized)], [.. greedy[i].Words.Select(w => w.Normalized)]);
                changed += diff.Errors;
            }

            scored.AddRange(report.Words.Select(w => new ScoredWord(w.Min, w.Label != WordLabel.Correct)));
        }

        return new DecodingRun
        {
            Name = name,
            Method = decoding.DecodingMethod,
            MaxActivePaths = decoding.DecodingMethod == RecognizerOptions.ModifiedBeamSearch ? decoding.MaxActivePaths : 0,
            Hotwords = hotwords,
            Clips = subset.Length,
            AudioSeconds = Math.Round(audioSeconds, 1),
            LoadMs = Math.Round(run.LoadMs, 1),
            DecodeMs = Math.Round(decodeMs, 1),
            Rtf = audioSeconds > 0 ? Math.Round(decodeMs / 1000.0 / audioSeconds, 5) : 0,
            ReferenceWords = refs,
            Errors = errors,
            Wer = refs > 0 ? Math.Round(errors / (double)refs, 4) : 0,
            ChangedWordsVsGreedy = changed,
            PeakRssMb = run.PeakRssMb,
            Confidence = ConfidenceMetrics.Summarize(scored),
        };
    }

    private static string Pct(int part, int whole) => whole == 0 ? "-" : (part / (double)whole).ToString("0.0%", CultureInfo.InvariantCulture);

    private static string Escape(string s) => s.Replace("|", "\\|", StringComparison.Ordinal);

    private static string SafeNativeVersion()
    {
        try
        {
            return OfflineRecognizer.NativeVersion;
        }
        catch (DllNotFoundException)
        {
            return "sherpa-onnx (not loaded)";
        }
    }
}

/// <summary>Owns a native recognizer for <see cref="ConfidenceEval"/>.</summary>
internal sealed class OwnedRecognizer(OfflineRecognizer recognizer) : ISpeechRecognizer, IDisposable
{
    public void Decode(ReadOnlySpan<ReadOnlyMemory<float>> utterances, Span<RecognitionResult> results) => recognizer.Decode(utterances, results);

    public void Dispose() => recognizer.Dispose();
}
