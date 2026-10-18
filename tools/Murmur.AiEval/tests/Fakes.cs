using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;

namespace Murmur.AiEval.Tests;

/// <summary>
/// Embeds by "concepts": dimension i+1 is set when the text contains every keyword of answerable question i, or the
/// question itself (queries embed the question text). Dimension 0 is a small bias so no vector is zero.
/// </summary>
internal sealed class ConceptEmbeddingGenerator(IReadOnlyList<EvalQuestion> questions, bool constant = false) : IEmbeddingGenerator<string, Embedding<float>>
{
    public int Inputs { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            Inputs++;
            var vector = new float[questions.Count + 1];
            vector[0] = 0.05f;
            for (var i = 0; !constant && i < questions.Count; i++)
            {
                var q = questions[i];
                if (!q.Answerable)
                {
                    continue;
                }

                var all = q.Keywords.Split(' ').All(k => value.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (all || value.Contains(q.Question, StringComparison.Ordinal))
                {
                    vector[i + 1] = 1;
                }
            }

            result.Add(new Embedding<float>(vector));
        }

        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>Answers from the prompt alone: cites the first excerpt's start, or says "not found" for the given questions.</summary>
internal sealed partial class ExcerptCitingChatClient(ISet<string> notFoundQuestions, string? fixedAnswer = null) : IChatClient
{
    public int Requests { get; private set; }

    [GeneratedRegex(@"Уривок 1 — «[^»]*», \[([0-9:]+)–")]
    private static partial Regex FirstExcerpt();

    [GeneratedRegex(@"Запитання: (.+)$")]
    private static partial Regex QuestionLine();

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer(messages))));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var answer = Answer(messages);
        foreach (var piece in answer.Chunk(7))
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, new string(piece));
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private string Answer(IEnumerable<ChatMessage> messages)
    {
        Requests++;
        if (fixedAnswer is not null)
        {
            return fixedAnswer;
        }

        var prompt = messages.Last().Text;
        var question = QuestionLine().Match(prompt).Groups[1].Value.Trim();
        if (notFoundQuestions.Contains(question))
        {
            return "У записах цього немає.";
        }

        var first = FirstExcerpt().Match(prompt);
        return first.Success ? $"Про це говорили на [{first.Groups[1].Value}]." : "Не знаю.";
    }
}

internal sealed class FakeLlm(IChatClient chat, IEmbeddingGenerator<string, Embedding<float>> embeddings) : ILlm
{
    public int ChatWarmUps { get; private set; }

    public LlmStatus Status => LlmStatus.Unloaded;

    public ValueTask<IChatClient> GetChatClientAsync(CancellationToken cancellationToken)
    {
        ChatWarmUps++;
        return ValueTask.FromResult(chat);
    }

    public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(embeddings);

    public bool Disposed { get; private set; }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
