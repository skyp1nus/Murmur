using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Storage.Tests;

public sealed class TranscriptStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 14, 9, 26, 53, TimeSpan.Zero);
    private readonly TempDataRoot _root = new();
    private readonly List<SqliteStorage> _opened = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var storage in _opened)
        {
            storage.Dispose();
        }

        _root.Dispose();
    }

    private SqliteStorage Open()
    {
        var storage = _root.Open();
        _opened.Add(storage);
        return storage;
    }

    private static TimeSpan Ms(long value) => TimeSpan.FromMilliseconds(value);

    private static TranscriptSegment Segment(Speaker speaker, long startMs, long endMs, string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words = new TranscriptWord[parts.Length];
        var step = parts.Length == 0 ? 0 : (endMs - startMs) / parts.Length;
        for (var i = 0; i < parts.Length; i++)
        {
            words[i] = new TranscriptWord(parts[i], Ms(startMs + (i * step)), Ms(startMs + ((i + 1) * step)));
        }

        return new TranscriptSegment(speaker, Ms(startMs), Ms(endMs), text, words);
    }

    private static async Task<SessionId> NewSession(SqliteStorage storage) =>
        (await storage.CreateSessionAsync("t", T0, Ct)).Id;

    private static async Task<IReadOnlyList<SegmentMatch>> Match(SqliteStorage storage, string query) =>
        await storage.MatchSegmentsAsync(query, 50, Ct);

    /// <summary>Runs a statement on a separate non-pooled connection and returns the first column of the first row.</summary>
    private long Scalar(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root.Path, "murmur.db"),
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    /// <summary>FTS5 raises an error when the index disagrees with the content table.</summary>
    private void AssertFtsConsistent() =>
        Scalar("INSERT INTO segments_fts (segments_fts, rank) VALUES ('integrity-check', 1)");

    [Fact]
    public async Task Summaries_GiveLanguageAndFirstLineOfEachTranscribedSession_InOneCall()
    {
        var storage = Open();
        var first = await NewSession(storage);
        var second = await NewSession(storage);
        var empty = await NewSession(storage);
        var none = await NewSession(storage);
        await storage.SaveTranscriptAsync(new Transcript(first, [Segment(Speaker.Others, 5000, 6000, "later"), Segment(Speaker.Me, 1000, 2000, "hello there")], "e", "en"), Ct);
        await storage.SaveTranscriptAsync(new Transcript(second, [Segment(Speaker.Me, 0, 1000, "привіт")], "e", null), Ct);
        await storage.SaveTranscriptAsync(new Transcript(empty, [], "e", "uk"), Ct);

        var summaries = await storage.GetTranscriptSummariesAsync([first, second, empty, none, SessionId.New()], Ct);

        Assert.Equal(3, summaries.Count);
        Assert.Equal(new TranscriptSummary(first, "en", "hello there"), summaries.Single(s => s.Session == first));
        Assert.Equal(new TranscriptSummary(second, null, "привіт"), summaries.Single(s => s.Session == second));
        Assert.Equal(new TranscriptSummary(empty, "uk", null), summaries.Single(s => s.Session == empty));
        Assert.Empty(await storage.GetTranscriptSummariesAsync([], Ct));
    }

    [Fact]
    public async Task Summaries_AnswerARepeatedIdOnce_AndStopWhenCancelled()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.SaveTranscriptAsync(new Transcript(id, [Segment(Speaker.Me, 0, 1000, "once")], "e", "en"), Ct);

        var summaries = await storage.GetTranscriptSummariesAsync([id, id, id], Ct);
        Assert.Single(summaries);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await storage.GetTranscriptSummariesAsync([id], cancelled.Token));
    }

    [Fact]
    public async Task Summaries_HandleMoreSessionsThanOneBatch()
    {
        var storage = Open();
        var ids = new List<SessionId>();
        for (var i = 0; i < 520; i++)
        {
            ids.Add(await NewSession(storage));
        }

        await storage.SaveTranscriptAsync(new Transcript(ids[^1], [Segment(Speaker.Me, 0, 1000, "last")], "e", "en"), Ct);

        var summaries = await storage.GetTranscriptSummariesAsync(ids, Ct);

        Assert.Equal("last", Assert.Single(summaries).FirstLine);
    }

    [Fact]
    public async Task Get_WithoutTranscript_ReturnsNull()
    {
        var storage = Open();
        var id = await NewSession(storage);

        Assert.Null(await storage.GetTranscriptAsync(id, Ct));
        Assert.Null(await storage.GetTranscriptAsync(SessionId.New(), Ct));
    }

    [Fact]
    public async Task Roundtrip_KeepsSpeakerTimesTextAndWords_IncludingCyrillicAndEmoji()
    {
        var storage = Open();
        var id = await NewSession(storage);
        var transcript = new Transcript(
            id,
            [
                Segment(Speaker.Me, 0, 3000, "Привіт усім, обговоримо бюджет"),
                Segment(Speaker.Others, 3500, 6000, "Sounds good 🎉 let's go"),
            ],
            "parakeet-tdt-0.6b-v3",
            "uk");

        await storage.SaveTranscriptAsync(transcript, Ct);
        var loaded = await storage.GetTranscriptAsync(id, Ct);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded.Session);
        Assert.Equal("parakeet-tdt-0.6b-v3", loaded.Engine);
        Assert.Equal("uk", loaded.Language);
        Assert.Equal(2, loaded.Segments.Count);
        for (var i = 0; i < 2; i++)
        {
            var expected = transcript.Segments[i];
            var actual = loaded.Segments[i];
            Assert.Equal(expected.Speaker, actual.Speaker);
            Assert.Equal(expected.Start, actual.Start);
            Assert.Equal(expected.End, actual.End);
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(expected.Words, actual.Words);
        }
    }

    [Fact]
    public async Task Roundtrip_SurvivesReopeningTheStorage_AndNullLanguage()
    {
        var first = Open();
        var id = await NewSession(first);
        await first.SaveTranscriptAsync(new Transcript(id, [Segment(Speaker.Me, 0, 1000, "hello world")], "e"), Ct);
        first.Dispose();

        var loaded = await Open().GetTranscriptAsync(id, Ct);

        Assert.NotNull(loaded);
        Assert.Null(loaded.Language);
        Assert.Equal("hello world", Assert.Single(loaded.Segments).Text);
    }

    [Fact]
    public async Task Segments_AreReturnedOrderedByStartThenIndex()
    {
        var storage = Open();
        var id = await NewSession(storage);
        var a = Segment(Speaker.Others, 5000, 6000, "third");
        var b = Segment(Speaker.Me, 1000, 2000, "first");
        var c = Segment(Speaker.Me, 5000, 5500, "fourth");
        var d = Segment(Speaker.Others, 2000, 3000, "second");

        await storage.SaveTranscriptAsync(new Transcript(id, [a, b, c, d], "e"), Ct);
        var loaded = await storage.GetTranscriptAsync(id, Ct);

        Assert.NotNull(loaded);
        Assert.Equal(["first", "second", "third", "fourth"], loaded.Segments.Select(s => s.Text));
    }

    [Fact]
    public async Task SegmentWithoutWords_Roundtrips()
    {
        var storage = Open();
        var id = await NewSession(storage);
        var segment = new TranscriptSegment(Speaker.Me, Ms(0), Ms(500), "text", []);

        await storage.SaveTranscriptAsync(new Transcript(id, [segment], "e"), Ct);
        var loaded = await storage.GetTranscriptAsync(id, Ct);

        Assert.NotNull(loaded);
        Assert.Empty(Assert.Single(loaded.Segments).Words);
    }

    [Fact]
    public async Task EmptyTranscript_IsStoredAndDistinctFromMissing()
    {
        var storage = Open();
        var id = await NewSession(storage);

        await storage.SaveTranscriptAsync(new Transcript(id, [], "parakeet", "en"), Ct);
        var loaded = await storage.GetTranscriptAsync(id, Ct);

        Assert.NotNull(loaded);
        Assert.Empty(loaded.Segments);
        Assert.Equal("parakeet", loaded.Engine);
        Assert.Equal("en", loaded.Language);
    }

    [Fact]
    public async Task Save_Replaces_OldSegmentsAndMetadata()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.SaveTranscriptAsync(
            new Transcript(id, [Segment(Speaker.Me, 0, 1000, "old one"), Segment(Speaker.Me, 1000, 2000, "old two")], "v1", "en"), Ct);

        await storage.SaveTranscriptAsync(new Transcript(id, [Segment(Speaker.Others, 0, 1000, "new only")], "v2"), Ct);
        var loaded = await storage.GetTranscriptAsync(id, Ct);

        Assert.NotNull(loaded);
        Assert.Equal("v2", loaded.Engine);
        Assert.Null(loaded.Language);
        var only = Assert.Single(loaded.Segments);
        Assert.Equal("new only", only.Text);
        Assert.Equal(Speaker.Others, only.Speaker);
        Assert.Equal(1L, Scalar("SELECT count(*) FROM segments"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM transcripts"));
        AssertFtsConsistent();
    }

    [Fact]
    public async Task Save_OtherSessionsAreUntouched()
    {
        var storage = Open();
        var a = await NewSession(storage);
        var b = await NewSession(storage);
        await storage.SaveTranscriptAsync(new Transcript(a, [Segment(Speaker.Me, 0, 1000, "alpha")], "e"), Ct);
        await storage.SaveTranscriptAsync(new Transcript(b, [Segment(Speaker.Me, 0, 1000, "beta")], "e"), Ct);

        await storage.SaveTranscriptAsync(new Transcript(a, [Segment(Speaker.Me, 0, 1000, "gamma")], "e"), Ct);

        Assert.Equal("beta", Assert.Single((await storage.GetTranscriptAsync(b, Ct))!.Segments).Text);
        Assert.Equal("gamma", Assert.Single((await storage.GetTranscriptAsync(a, Ct))!.Segments).Text);
    }

    [Fact]
    public async Task Save_ForUnknownSession_Throws_AndStoresNothing()
    {
        var storage = Open();
        await NewSession(storage);
        var transcript = new Transcript(SessionId.New(), [Segment(Speaker.Me, 0, 1000, "orphan")], "e");

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await storage.SaveTranscriptAsync(transcript, Ct));

        Assert.Equal(0L, Scalar("SELECT count(*) FROM segments"));
    }

    [Fact]
    public async Task Save_WithFailingSegment_RollsBackAndKeepsPreviousTranscript()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.SaveTranscriptAsync(new Transcript(id, [Segment(Speaker.Me, 0, 1000, "keepme")], "v1"), Ct);
        var tooLong = new TranscriptWord(new string('x', ushort.MaxValue + 1), Ms(0), Ms(1));
        var bad = new Transcript(id, [Segment(Speaker.Me, 0, 1000, "fine"), new TranscriptSegment(Speaker.Me, Ms(1), Ms(2), "bad", [tooLong])], "v2");

        await Assert.ThrowsAsync<ArgumentException>(async () => await storage.SaveTranscriptAsync(bad, Ct));

        var loaded = await storage.GetTranscriptAsync(id, Ct);
        Assert.NotNull(loaded);
        Assert.Equal("v1", loaded.Engine);
        Assert.Equal("keepme", Assert.Single(loaded.Segments).Text);
        Assert.Single(await Match(storage, "keepme"));
        Assert.Empty(await Match(storage, "fine"));
    }

    [Fact]
    public async Task Delete_RemovesSegmentsTranscriptAndFtsRows()
    {
        var storage = Open();
        var doomed = await NewSession(storage);
        var kept = await NewSession(storage);
        await storage.SaveTranscriptAsync(new Transcript(doomed, [Segment(Speaker.Me, 0, 1000, "бюджет проєкту")], "e"), Ct);
        await storage.SaveTranscriptAsync(new Transcript(kept, [Segment(Speaker.Me, 0, 1000, "бюджет залишається")], "e"), Ct);
        Assert.Equal(2, (await Match(storage, "бюджет")).Count);

        await storage.DeleteSessionAsync(doomed, Ct);

        Assert.Null(await storage.GetTranscriptAsync(doomed, Ct));
        Assert.NotNull(await storage.GetTranscriptAsync(kept, Ct));
        var hit = Assert.Single(await Match(storage, "бюджет"));
        Assert.Equal(kept, hit.Session);
        Assert.Empty(await Match(storage, "проєкту"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM segments"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM transcripts"));
        AssertFtsConsistent();
    }

    [Fact]
    public async Task Fts_FindsUkrainianWords_CaseAndDiacriticsInsensitive()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.SaveTranscriptAsync(
            new Transcript(
                id,
                [
                    Segment(Speaker.Me, 0, 2000, "Давайте обговоримо Бюджет на наступний квартал"),
                    Segment(Speaker.Others, 2000, 4000, "Спершу розглянемо дедлайни"),
                    Segment(Speaker.Others, 4000, 6000, "Café naïve résumé"),
                ],
                "e",
                "uk"),
            Ct);

        var budget = Assert.Single(await Match(storage, "бюджет"));
        Assert.Equal(id, budget.Session);
        Assert.Equal(0, budget.Index);
        Assert.Equal(Ms(0), budget.Start);
        Assert.Contains("Бюджет", budget.Text);
        Assert.Single(await Match(storage, "дедлайни"));
        Assert.Single(await Match(storage, "бюд*"));
        Assert.Empty(await Match(storage, "відсутнє"));
        Assert.Single(await Match(storage, "cafe"));
        Assert.Single(await Match(storage, "RESUME"));
    }

    [Fact]
    public async Task Fts_IsUpdatedOnReplace()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.SaveTranscriptAsync(new Transcript(id, [Segment(Speaker.Me, 0, 1000, "обговоримо бюджет")], "e"), Ct);
        Assert.Single(await Match(storage, "бюджет"));

        await storage.SaveTranscriptAsync(new Transcript(id, [Segment(Speaker.Me, 0, 1000, "обговоримо дедлайни")], "e"), Ct);

        Assert.Empty(await Match(storage, "бюджет"));
        Assert.Single(await Match(storage, "дедлайни"));

        await storage.SaveTranscriptAsync(new Transcript(id, [], "e"), Ct);
        Assert.Empty(await Match(storage, "дедлайни"));
        Assert.Empty(await Match(storage, "обговоримо"));
        AssertFtsConsistent();
    }

    [Fact]
    public async Task ConcurrentSaveAndGet_NeverObservesAPartialTranscript()
    {
        var storage = Open();
        var id = await NewSession(storage);
        const int perTranscript = 50;

        Transcript Make(int generation)
        {
            var segments = new TranscriptSegment[perTranscript];
            for (var i = 0; i < perTranscript; i++)
            {
                segments[i] = Segment(Speaker.Me, i * 1000L, (i * 1000L) + 900, "gen" + generation + " word" + i);
            }

            return new Transcript(id, segments, "gen" + generation);
        }

        await storage.SaveTranscriptAsync(Make(0), Ct);
        var writers = Enumerable.Range(1, 4).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                await storage.SaveTranscriptAsync(Make((w * 100) + i), Ct);
            }
        }, Ct)).ToArray();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++)
            {
                var loaded = await storage.GetTranscriptAsync(id, Ct);
                Assert.NotNull(loaded);
                Assert.Equal(perTranscript, loaded.Segments.Count);
                Assert.All(loaded.Segments, s => Assert.StartsWith(loaded.Engine + " ", s.Text, StringComparison.Ordinal));
            }
        }, Ct)).ToArray();

        await Task.WhenAll([.. writers, .. readers]);

        Assert.Equal(perTranscript, Scalar("SELECT count(*) FROM segments"));
        Assert.Equal(perTranscript, Scalar("SELECT count(*) FROM segments_fts WHERE segments_fts MATCH 'gen*'"));
        AssertFtsConsistent();
    }

    [Fact]
    public async Task SaveAndLoad_OneHourTranscript_IsFast()
    {
        var storage = Open();
        var id = await NewSession(storage);
        var transcript = BuildHourTranscript(id);

        var saveMs = new List<double>();
        var loadMs = new List<double>();
        Transcript? loaded = null;
        for (var run = 0; run < 5; run++)
        {
            var sw = Stopwatch.StartNew();
            await storage.SaveTranscriptAsync(transcript, Ct);
            saveMs.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            loaded = await storage.GetTranscriptAsync(id, Ct);
            loadMs.Add(sw.Elapsed.TotalMilliseconds);
        }

        Assert.NotNull(loaded);
        var words = transcript.Segments.Sum(s => s.Words.Count);
        Assert.Equal(transcript.Segments.Count, loaded.Segments.Count);
        Assert.Equal(words, loaded.Segments.Sum(s => s.Words.Count));
        var bestSave = saveMs.Min();
        var bestLoad = loadMs.Min();
        var report = string.Format(
            CultureInfo.InvariantCulture,
            "1h transcript ({0} segments, {1} words): save min {2:F1} ms (runs {3}), load min {4:F1} ms (runs {5})",
            transcript.Segments.Count,
            words,
            bestSave,
            string.Join("/", saveMs.Select(m => m.ToString("F0", CultureInfo.InvariantCulture))),
            bestLoad,
            string.Join("/", loadMs.Select(m => m.ToString("F0", CultureInfo.InvariantCulture))));
        TestContext.Current.SendDiagnosticMessage(report);
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        Assert.True(bestSave < 900, $"save took {bestSave:F1} ms (budget 300 ms x3)");
        Assert.True(bestLoad < 300, $"load took {bestLoad:F1} ms (budget 100 ms x3)");
    }

    private static Transcript BuildHourTranscript(SessionId id)
    {
        string[] vocabulary = ["бюджет", "проєкт", "команда", "meeting", "deadline", "давайте", "обговоримо", "release", "квартал", "що", "ми", "the"];
        var segments = new List<TranscriptSegment>(900);
        var random = new Random(42);
        for (var i = 0; i < 900; i++)
        {
            var start = i * 4000L;
            var count = 10 + (i % 3); // about 10k words in total
            var text = string.Join(' ', Enumerable.Range(0, count).Select(_ => vocabulary[random.Next(vocabulary.Length)]));
            segments.Add(Segment(i % 2 == 0 ? Speaker.Me : Speaker.Others, start, start + 3800, text));
        }

        return new Transcript(id, segments, "parakeet-tdt-0.6b-v3", "uk");
    }
}
