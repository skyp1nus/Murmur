using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Intelligence;
using Murmur.Intelligence.Rag;
using Murmur.Storage;

namespace Murmur.AiEval;

/// <summary>Which models are installed (server binary plus the model file).</summary>
public sealed record LlmAvailability(bool Chat, bool Embedding)
{
    public static LlmAvailability From(LocalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var server = Exists(options.LlamaServerPath);
        return new LlmAvailability(server && Exists(options.ChatModelPath), server && Exists(options.EmbeddingModelPath));
    }

    public static ModelFiles Describe(LocalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ModelFiles(Describe(options.ChatModelPath), Describe(options.EmbeddingModelPath), Describe(options.LlamaServerPath));
    }

    private static ModelFile? Describe(string? path) =>
        string.IsNullOrEmpty(path) ? null : new ModelFile(Path.GetFileName(path), path, Exists(path) ? new FileInfo(path).Length : null);

    private static bool Exists(string? path) => !string.IsNullOrEmpty(path) && File.Exists(path);
}

/// <summary>The evaluation cannot run (missing models, unreadable inputs); exit code 2.</summary>
public sealed class EvalSetupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Runs the evaluation end to end against the real <see cref="SqliteStorage"/> and <see cref="Assistant"/>:
/// stores the fixture as a session transcript, indexes it, then asks (full mode) or searches (retrieval modes)
/// every question and scores the result.
/// </summary>
public static class EvalRunner
{
    private static readonly DateTimeOffset SessionStart = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    public static async Task<EvalReport> RunAsync(
        EvalOptions options, ILlm llm, LlmAvailability availability, ModelFiles models, TextWriter log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(log);

        var mode = options.RetrievalOnly
            ? availability.Embedding ? EvalMode.RetrievalHybrid : EvalMode.RetrievalKeyword
            : EvalMode.Full;
        if (mode == EvalMode.Full && !(availability.Chat && availability.Embedding))
        {
            throw new EvalSetupException(
                "The full evaluation needs llama-server, the chat model and the embedding model " +
                "(MURMUR_LLAMA_SERVER, MURMUR_LLM_MODEL, MURMUR_EMBED_MODEL). Use --retrieval-only without models.");
        }

        var fixture = Load(options.FixturePath, EvalJsonContext.Default.FixtureTranscript);
        var questionSet = Load(options.QuestionsPath, EvalJsonContext.Default.QuestionSet);
        if (fixture.Segments.Count == 0 || questionSet.Questions.Count == 0)
        {
            throw new EvalSetupException("The fixture has no segments or the questions file has no questions.");
        }

        var total = Stopwatch.StartNew();
        var ownsDataDir = options.DataDir is null;
        var dataDir = options.DataDir ?? Directory.CreateTempSubdirectory("murmur-aieval-").FullName;
        try
        {
            using var storage = new SqliteStorage(dataDir);
            var session = await storage.CreateSessionAsync(fixture.Title, SessionStart, cancellationToken).ConfigureAwait(false);
            await storage.UpdateSessionAsync(
                session with
                {
                    Duration = TimeSpan.FromMilliseconds(fixture.DurationMs),
                    State = SessionState.Transcribed,
                    HasMicrophone = true,
                    HasSystemAudio = true,
                },
                cancellationToken).ConfigureAwait(false);

            var transcript = fixture.ToTranscript(session.Id);
            var watch = Stopwatch.StartNew();
            await storage.SaveTranscriptAsync(transcript, cancellationToken).ConfigureAwait(false);
            var saveMs = watch.ElapsedMilliseconds;
            log.WriteLine($"[ai-eval] mode {mode}: stored {fixture.Segments.Count} segments in {saveMs} ms ({dataDir})");

            var assistantOptions = new AssistantOptions { MaxOutputTokens = options.MaxOutputTokens };
            var observed = new ObservedLlm(llm);
            var assistant = new Assistant(observed, storage, assistantOptions);
            var chunks = new TranscriptChunker(assistantOptions.ChunkMaxDuration).Chunk(transcript).Count;

            long indexMs = 0;
            if (mode != EvalMode.RetrievalKeyword)
            {
                log.WriteLine($"[ai-eval] indexing {chunks} chunks (starts the embedding server)…");
                watch.Restart();
                await assistant.IndexSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                indexMs = watch.ElapsedMilliseconds;
                log.WriteLine($"[ai-eval] indexed in {indexMs} ms");
            }

            long chatLoadMs = 0;
            if (mode == EvalMode.Full)
            {
                log.WriteLine("[ai-eval] loading the chat model…");
                watch.Restart();
                await llm.GetChatClientAsync(cancellationToken).ConfigureAwait(false);
                chatLoadMs = watch.ElapsedMilliseconds;
                log.WriteLine($"[ai-eval] chat model ready in {chatLoadMs} ms");
            }

            var results = new List<QuestionResult>(questionSet.Questions.Count);
            foreach (var question in questionSet.Questions)
            {
                var result = mode == EvalMode.Full
                    ? await AskAsync(assistant, observed, session.Id, question, cancellationToken).ConfigureAwait(false)
                    : await SearchAsync(assistant, session.Id, question, mode, options.TopK, cancellationToken).ConfigureAwait(false);
                results.Add(result);
                log.WriteLine(
                    $"[ai-eval] {result.Id} {(result.Skipped ? "SKIP" : result.Pass ? "PASS" : "FAIL")} " +
                    $"expected {result.Expected ?? "not in recording"}, cited [{string.Join(", ", result.Cited)}], {result.LatencyMs} ms" +
                    (mode == EvalMode.Full
                        ? $" (first token {result.FirstTokenMs?.ToString(CultureInfo.InvariantCulture) ?? "-"} ms, finish {result.FinishReason ?? "-"}, " +
                          $"tokens in/out {result.InputTokens?.ToString(CultureInfo.InvariantCulture) ?? "-"}/{result.OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "-"}, " +
                          $"reasoning {result.ReasoningChars ?? 0} chars)"
                        : string.Empty));
            }

            var scored = results.Where(r => !r.Skipped).ToList();
            var passed = scored.Count(r => r.Pass);
            var answers = mode == EvalMode.Full ? results.Select(r => r.LatencyMs).ToList() : [];
            return new EvalReport
            {
                Mode = mode,
                Passed = passed,
                Total = scored.Count,
                Rate = Math.Round(Scoring.Rate(passed, scored.Count), 4),
                ExplicitPassed = scored.Count(r => r.Pass && r.MatchedExplicitly),
                Threshold = options.Threshold,
                Success = Scoring.Succeeded(passed, scored.Count, options.Threshold),
                Fixture = Path.GetFileName(options.FixturePath),
                Segments = fixture.Segments.Count,
                Models = models,
                Timings = new EvalTimings
                {
                    SaveMs = saveMs,
                    IndexMs = indexMs,
                    Chunks = chunks,
                    ChatLoadMs = chatLoadMs,
                    AvgAnswerMs = answers.Count == 0 ? 0 : (long)answers.Average(),
                    MaxAnswerMs = answers.Count == 0 ? 0 : answers.Max(),
                    TotalMs = total.ElapsedMilliseconds,
                },
                Machine = new MachineInfo(
                    RuntimeInformation.OSDescription,
                    RuntimeInformation.OSArchitecture.ToString(),
                    Environment.ProcessorCount,
                    RuntimeInformation.FrameworkDescription),
                PerQuestion = results,
            };
        }
        finally
        {
            if (ownsDataDir)
            {
                TryDelete(dataDir);
            }
        }
    }

    private static async Task<QuestionResult> AskAsync(
        Assistant assistant, ObservedLlm observed, SessionId session, EvalQuestion question, CancellationToken cancellationToken)
    {
        var answer = new StringBuilder();
        IReadOnlyList<Citation> citations = [];
        long? firstToken = null;
        var watch = Stopwatch.StartNew();
        string? error = null;
        try
        {
            await foreach (var update in assistant.AskAsync(new AskRequest(question.Question, session), cancellationToken).ConfigureAwait(false))
            {
                if (update.IsFinal)
                {
                    citations = update.Citations;
                    continue;
                }

                firstToken ??= watch.ElapsedMilliseconds;
                answer.Append(update.TextDelta);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // One failed answer (e.g. a server hiccup) fails that question, not the whole run.
            error = $"ERROR {ex.GetType().Name}: {ex.Message}";
        }

        var latency = watch.ElapsedMilliseconds;
        var stats = observed.Last;
        var text = answer.ToString().Trim();
        var score = error is null ? Scoring.ScoreAnswer(question, text, citations) : new QuestionScore(false, false, [], false, null);
        return new QuestionResult
        {
            Id = question.Id,
            Question = question.Question,
            Answerable = question.Answerable,
            Expected = question.Expected,
            ExpectedMs = question.ExpectedMs,
            ToleranceSeconds = question.ToleranceSeconds,
            Query = question.Question,
            Cited = score.Cited,
            ExplicitCitation = score.ExplicitCitation,
            MatchedExplicitly = score.MatchedExplicitly,
            SaysNotFound = score.SaysNotFound,
            ConveysFact = score.ConveysFact,
            Pass = score.Pass,
            LatencyMs = latency,
            FirstTokenMs = firstToken,
            FinishReason = stats?.FinishReason,
            InputTokens = stats?.InputTokens,
            OutputTokens = stats?.OutputTokens,
            ReasoningChars = stats?.ReasoningChars,
            Answer = error ?? text,
        };
    }

    private static async Task<QuestionResult> SearchAsync(
        Assistant assistant, SessionId session, EvalQuestion question, EvalMode mode, int topK, CancellationToken cancellationToken)
    {
        var keywordMode = mode == EvalMode.RetrievalKeyword;
        var query = keywordMode ? question.Keywords : question.Question;
        var watch = Stopwatch.StartNew();
        var hits = await assistant.SearchAsync(query, session, topK, cancellationToken).ConfigureAwait(false);
        var latency = watch.ElapsedMilliseconds;
        var score = Scoring.ScoreRetrieval(question, hits, keywordMode);
        return new QuestionResult
        {
            Id = question.Id,
            Question = question.Question,
            Answerable = question.Answerable,
            Expected = question.Expected,
            ExpectedMs = question.ExpectedMs,
            ToleranceSeconds = question.ToleranceSeconds,
            Query = query,
            Cited = score.Cited,
            Pass = score.Pass,
            Skipped = score.Skipped,
            LatencyMs = latency,
            Answer = hits.Count == 0 ? "(no hits)" : hits[0].Chunk.Text.ReplaceLineEndings(" "),
        };
    }

    private static T Load<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, typeInfo) ?? throw new EvalSetupException($"{path} is empty.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new EvalSetupException($"Cannot read {path}: {ex.Message}", ex);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder is harmless.
        }
    }
}
