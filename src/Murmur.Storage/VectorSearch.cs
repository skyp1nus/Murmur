using System.Buffers.Binary;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Murmur.Core.Sessions;

namespace Murmur.Storage;

/// <summary>Position and text of one embedded chunk (the vector itself lives in <see cref="SessionVectors.Data"/>).</summary>
internal readonly record struct ChunkInfo(long StartMs, long EndMs, string Text);

/// <summary>
/// Immutable in-memory copy of the embedded chunks of one session: <see cref="Count"/> L2-normalized vectors of
/// <see cref="Dim"/> floats laid out contiguously in <see cref="Data"/>, row i belonging to <c>Chunks[i]</c>.
/// </summary>
internal sealed class SessionVectors
{
    public SessionVectors(SessionId session, int dim, float[] data, ChunkInfo[] chunks)
    {
        Session = session;
        Dim = dim;
        Data = data;
        Chunks = chunks;
    }

    public SessionId Session { get; }

    public int Dim { get; }

    public float[] Data { get; }

    public ChunkInfo[] Chunks { get; }

    public int Count => Chunks.Length;
}

/// <summary>Managed SIMD brute-force cosine search (decision D-017) and the float32 BLOB codec.</summary>
internal static class VectorSearch
{
    /// <summary>
    /// L2-normalizes <paramref name="vector"/> in place. Returns <see langword="false"/> when it holds a NaN or an
    /// infinity. A zero vector stays zero (it scores 0 against everything).
    /// </summary>
    public static bool TryNormalize(Span<float> vector)
    {
        var squared = TensorPrimitives.Dot<float>(vector, vector);
        if (!float.IsFinite(squared))
        {
            return false;
        }

        if (squared > 0f)
        {
            TensorPrimitives.Multiply(vector, 1f / MathF.Sqrt(squared), vector);
        }

        return true;
    }

    /// <summary>Little-endian float32 encoding, the on-disk vector format.</summary>
    public static void ToBytes(ReadOnlySpan<float> source, Span<byte> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.AsBytes(source).CopyTo(destination);
            return;
        }

        for (var i = 0; i < source.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(destination[(i * sizeof(float))..], source[i]);
        }
    }

    public static void FromBytes(ReadOnlySpan<byte> source, Span<float> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            source[..(destination.Length * sizeof(float))].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = BinaryPrimitives.ReadSingleLittleEndian(source[(i * sizeof(float))..]);
        }
    }

    /// <summary>
    /// Top <paramref name="limit"/> chunks by dot product with the (already normalized) <paramref name="query"/>,
    /// best first. Sessions embedded with another dimension are skipped. Allocates only the small heap and the result.
    /// </summary>
    public static List<SearchHit> Search(ReadOnlySpan<SessionVectors> sources, ReadOnlySpan<float> query, int limit)
    {
        if (query.IsEmpty)
        {
            return [];
        }

        long total = 0;
        foreach (var source in sources)
        {
            if (source.Dim == query.Length)
            {
                total += source.Count;
            }
        }

        var k = (int)Math.Min(limit, total);
        if (k <= 0)
        {
            return [];
        }

        // Min-heap of the k best so far: the root is the worst of them, so most rows are rejected with one compare.
        var scores = new float[k];
        var owners = new int[k];
        var rows = new int[k];
        var size = 0;

        for (var s = 0; s < sources.Length; s++)
        {
            var source = sources[s];
            if (source.Dim != query.Length)
            {
                continue;
            }

            var data = source.Data.AsSpan();
            var dim = source.Dim;
            for (var row = 0; row < source.Count; row++)
            {
                var score = TensorPrimitives.Dot(query, data.Slice(row * dim, dim));
                if (size < k)
                {
                    SiftUp(scores, owners, rows, size++, score, s, row);
                }
                else if (score > scores[0])
                {
                    SiftDown(scores, owners, rows, size, score, s, row);
                }
            }
        }

        // Pop the min-heap repeatedly: the results come out worst first, so reverse at the end.
        var hits = new List<SearchHit>(size);
        for (var last = size - 1; last >= 0; last--)
        {
            var topScore = scores[0];
            var topOwner = owners[0];
            var topRow = rows[0];
            if (last > 0)
            {
                SiftDown(scores, owners, rows, last, scores[last], owners[last], rows[last]);
            }

            var chunk = sources[topOwner].Chunks[topRow];
            hits.Add(new SearchHit(
                new TextChunk(
                    sources[topOwner].Session,
                    TimeSpan.FromMilliseconds(chunk.StartMs),
                    TimeSpan.FromMilliseconds(chunk.EndMs),
                    chunk.Text),
                topScore,
                SearchSource.Vector));
        }

        hits.Reverse();
        return hits;
    }

    private static void SiftUp(float[] scores, int[] owners, int[] rows, int index, float score, int owner, int row)
    {
        while (index > 0)
        {
            var parent = (index - 1) / 2;
            if (scores[parent] <= score)
            {
                break;
            }

            scores[index] = scores[parent];
            owners[index] = owners[parent];
            rows[index] = rows[parent];
            index = parent;
        }

        scores[index] = score;
        owners[index] = owner;
        rows[index] = row;
    }

    /// <summary>Places the item at the root of a min-heap of <paramref name="size"/> elements and restores the heap.</summary>
    private static void SiftDown(float[] scores, int[] owners, int[] rows, int size, float score, int owner, int row)
    {
        var index = 0;
        while (true)
        {
            var child = (2 * index) + 1;
            if (child >= size)
            {
                break;
            }

            if (child + 1 < size && scores[child + 1] < scores[child])
            {
                child++;
            }

            if (scores[child] >= score)
            {
                break;
            }

            scores[index] = scores[child];
            owners[index] = owners[child];
            rows[index] = rows[child];
            index = child;
        }

        scores[index] = score;
        owners[index] = owner;
        rows[index] = row;
    }
}
