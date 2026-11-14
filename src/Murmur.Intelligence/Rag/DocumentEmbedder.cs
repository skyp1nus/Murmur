using Murmur.Core.Intelligence;

namespace Murmur.Intelligence.Rag;

/// <summary>Embeds chunk texts as documents (with the <see cref="EmbeddingPrompts.Document"/> prompt) in batches; shared by indexing and the chunk refresh.</summary>
internal static class DocumentEmbedder
{
    /// <summary>One vector per text, in order. Does not ask the model for a generator when <paramref name="texts"/> is empty.</summary>
    public static async ValueTask<List<ReadOnlyMemory<float>>> EmbedAsync(ILlm llm, IReadOnlyList<string> texts, int batchSize, CancellationToken cancellationToken)
    {
        var embeddings = new List<ReadOnlyMemory<float>>(texts.Count);
        if (texts.Count == 0)
        {
            return embeddings;
        }

        var generator = await llm.GetEmbeddingGeneratorAsync(cancellationToken).ConfigureAwait(false);
        batchSize = Math.Max(1, batchSize);
        for (var offset = 0; offset < texts.Count; offset += batchSize)
        {
            var count = Math.Min(batchSize, texts.Count - offset);
            var inputs = new string[count];
            for (var i = 0; i < count; i++)
            {
                inputs[i] = EmbeddingPrompts.Document(texts[offset + i]);
            }

            var generated = await generator.GenerateAsync(inputs, options: null, cancellationToken).ConfigureAwait(false);
            if (generated.Count != count)
            {
                throw new InvalidDataException($"Expected {count} embeddings, got {generated.Count}.");
            }

            foreach (var embedding in generated)
            {
                embeddings.Add(embedding.Vector);
            }
        }

        return embeddings;
    }
}
