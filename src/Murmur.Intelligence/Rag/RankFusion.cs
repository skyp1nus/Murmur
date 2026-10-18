using Murmur.Core.Sessions;

namespace Murmur.Intelligence.Rag;

/// <summary>
/// Reciprocal Rank Fusion of BM25 hits (one transcript segment each) and vector hits (one chunk each). A keyword hit
/// that overlaps a vector chunk of the same session in time counts as the same result and lifts that chunk; other
/// keyword hits stay results of their own. Score = Σ 1 / (k + rank), rank starting at 1, one term per list.
/// </summary>
public static class RankFusion
{
    public static IReadOnlyList<SearchHit> Fuse(
        IReadOnlyList<SearchHit> keyword, IReadOnlyList<SearchHit> vector, int k, int limit, SessionId? scope = null)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        if (limit <= 0)
        {
            return [];
        }

        var groups = new List<Group>();
        var rank = 0;
        foreach (var hit in Ranked(vector, scope))
        {
            rank++;
            if (groups.Exists(g => g.Chunk == hit.Chunk))
            {
                continue;
            }

            groups.Add(new Group(hit.Chunk, 1.0 / (k + rank), rank, groups.Count) { HasVector = true });
        }

        rank = 0;
        foreach (var hit in Ranked(keyword, scope))
        {
            rank++;
            var target = groups.Find(g => g.HasVector && g.Chunk.Session == hit.Chunk.Session && Overlaps(g.Chunk, hit.Chunk))
                ?? groups.Find(g => !g.HasVector && g.Chunk == hit.Chunk);
            if (target is null)
            {
                groups.Add(new Group(hit.Chunk, 1.0 / (k + rank), rank, groups.Count) { HasKeyword = true });
            }
            else if (!target.HasKeyword)
            {
                target.Score += 1.0 / (k + rank);
                target.BestRank = Math.Min(target.BestRank, rank);
                target.HasKeyword = true;
            }
        }

        groups.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var byRank = a.BestRank.CompareTo(b.BestRank);
            return byRank != 0 ? byRank : a.Order.CompareTo(b.Order);
        });

        var count = Math.Min(limit, groups.Count);
        var result = new SearchHit[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = new SearchHit(groups[i].Chunk, groups[i].Score, SearchSource.Hybrid);
        }

        return result;
    }

    /// <summary>Inclusive time overlap, so a zero-length keyword hit on a chunk edge still matches.</summary>
    public static bool Overlaps(TextChunk a, TextChunk b) => a.Start <= b.End && b.Start <= a.End;

    private static IEnumerable<SearchHit> Ranked(IReadOnlyList<SearchHit> hits, SessionId? scope)
    {
        // Stable sort by score, best first; the stores already return them in that order.
        var ordered = new List<SearchHit>(hits.Count);
        foreach (var hit in hits)
        {
            if (scope is null || hit.Chunk.Session == scope.Value)
            {
                ordered.Add(hit);
            }
        }

        var indexed = new (SearchHit Hit, int Index)[ordered.Count];
        for (var i = 0; i < ordered.Count; i++)
        {
            indexed[i] = (ordered[i], i);
        }

        Array.Sort(indexed, static (a, b) =>
        {
            var byScore = b.Hit.Score.CompareTo(a.Hit.Score);
            return byScore != 0 ? byScore : a.Index.CompareTo(b.Index);
        });

        foreach (var (hit, _) in indexed)
        {
            yield return hit;
        }
    }

    private sealed class Group(TextChunk chunk, double score, int rank, int order)
    {
        public TextChunk Chunk { get; } = chunk;

        public double Score { get; set; } = score;

        public int BestRank { get; set; } = rank;

        public int Order { get; } = order;

        public bool HasVector { get; init; }

        public bool HasKeyword { get; set; }
    }
}
