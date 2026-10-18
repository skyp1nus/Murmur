using BenchmarkDotNet.Attributes;
using Murmur.Core.Sessions;
using Murmur.Storage;

namespace Murmur.Benchmarks;

/// <summary>
/// Brute-force cosine search (D-017): 10 000 chunks x 768 dims in 20 sessions, top-10. <see cref="ScanOnly"/> is the
/// SIMD scan and heap; <see cref="StorageSearch"/> is the full ISearchIndex call (thread-pool hop, query
/// normalization, warm cache, hits).
/// </summary>
[MemoryDiagnoser]
public class VectorSearchBenchmarks
{
    private const int Chunks = 10_000;
    private const int Dim = 768;
    private const int Sessions = 20;

    private string _root = string.Empty;
    private SqliteStorage _storage = null!;
    private SessionVectors[] _sources = null!;
    private float[] _query = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var random = new Random(7);
        _root = Path.Combine(Path.GetTempPath(), "murmur-bench-" + Guid.NewGuid().ToString("N"));
        _storage = new SqliteStorage(_root);

        var perSession = Chunks / Sessions;
        _sources = new SessionVectors[Sessions];
        for (var s = 0; s < Sessions; s++)
        {
            var session = (await _storage.CreateSessionAsync("bench", DateTimeOffset.UtcNow, CancellationToken.None)).Id;
            var chunks = new List<TextChunk>(perSession);
            var embeddings = new List<ReadOnlyMemory<float>>(perSession);
            var data = new float[perSession * Dim];
            var infos = new ChunkInfo[perSession];
            for (var i = 0; i < perSession; i++)
            {
                var v = RandomVector(random);
                chunks.Add(new TextChunk(session, TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i + 1), "chunk " + i));
                embeddings.Add(v);
                v.CopyTo(data.AsSpan(i * Dim, Dim));
                VectorSearch.TryNormalize(data.AsSpan(i * Dim, Dim));
                infos[i] = new ChunkInfo(i * 1000, (i * 1000) + 1000, "chunk " + i);
            }

            await _storage.ReplaceChunksAsync(session, chunks, embeddings, CancellationToken.None);
            _sources[s] = new SessionVectors(session, Dim, data, infos);
        }

        _query = RandomVector(random);
        VectorSearch.TryNormalize(_query);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _storage.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Benchmark]
    public int ScanOnly() => VectorSearch.Search(_sources, _query, 10).Count;

    [Benchmark]
    public async Task<int> StorageSearch() =>
        (await _storage.SearchVectorAsync(_query, null, 10, CancellationToken.None)).Count;

    private static float[] RandomVector(Random random)
    {
        var v = new float[Dim];
        for (var i = 0; i < Dim; i++)
        {
            v[i] = (float)((random.NextDouble() * 2) - 1);
        }

        return v;
    }
}
