using Microsoft.Data.Sqlite;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Storage.Tests;

/// <summary>#58: per-word confidence through the store, the fix of one word (ReplaceWordAsync) and the user's vocabulary.</summary>
public sealed class UncertainWordStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
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

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private static TranscriptWord W(string text, int start, float? confidence = null) =>
        new(text, Ms(start), Ms(start + 300)) { Confidence = confidence };

    /// <summary>Two lines; "parquet" (0.31) is the doubtful word of the second one.</summary>
    private static async Task<SessionId> WithTranscript(SqliteStorage storage)
    {
        var id = (await storage.CreateSessionAsync("t", T0, Ct)).Id;
        await storage.SaveTranscriptAsync(
            new Transcript(
                id,
                [
                    new TranscriptSegment(Speaker.Me, Ms(0), Ms(2000), "Okay, a quick run.", [W("Okay,", 0, 0.97f), W("a", 400, 0.99f), W("quick", 800, 0.95f), W("run.", 1200, 0.9f)]),
                    new TranscriptSegment(Speaker.Others, Ms(3000), Ms(6000), "Does it run on the  parquet model?", [W("Does", 3000, 0.99f), W("it", 3300), W("run", 3600, 0.93f), W("on", 3900, 0.98f), W("the", 4200, 0.97f), W("parquet", 4500, 0.31f), W("model?", 4800, 0.88f)]),
                ],
                "parakeet",
                "en"),
            Ct);
        return id;
    }

    [Fact]
    public async Task TheConfidenceOfEveryWord_IsStoredAndReadBack()
    {
        var storage = Open();
        var id = await WithTranscript(storage);

        var transcript = await storage.GetTranscriptAsync(id, Ct);

        var words = transcript!.Segments[1].Words;
        Assert.Null(words[1].Confidence);
        Assert.Equal(0.31f, words[5].Confidence!.Value, 1f / 254);
        Assert.True(words[5].IsUncertain);
        Assert.Equal([false, false, false, false, false, true, false], words.Select(w => w.IsUncertain));
    }

    [Fact]
    public async Task ReplaceWord_ChangesTheWord_TheLineAndTheIndex_AndConfirmsTheWord()
    {
        var storage = Open();
        var id = await WithTranscript(storage);

        await storage.ReplaceWordAsync(id, 1, 5, " Parakeet ", Ct);

        var segment = (await storage.GetTranscriptAsync(id, Ct))!.Segments[1];
        Assert.Equal("Parakeet", segment.Words[5].Text);
        Assert.Null(segment.Words[5].Confidence);
        Assert.False(segment.Words[5].IsUncertain);

        // The rest of the line, its spacing and the other words' confidence stay.
        Assert.Equal("Does it run on the  Parakeet model?", segment.Text);
        Assert.Equal(0.88f, segment.Words[6].Confidence!.Value, 1f / 254);
        Assert.Equal(Ms(4500), segment.Words[5].Start);

        Assert.Empty(await storage.SearchKeywordAsync("parquet", null, 10, Ct));
        var hit = Assert.Single(await storage.SearchKeywordAsync("parakeet", null, 10, Ct));
        Assert.Equal(id, hit.Chunk.Session);
        Assert.Equal("Does it run on the  Parakeet model?", hit.Chunk.Text);
    }

    [Fact]
    public async Task ReplaceWord_KeepsTheSummaryTheChaptersAndTheOtherLines()
    {
        var storage = Open();
        var id = await WithTranscript(storage);
        var summary = new SessionSummary(id, "Roadmap.", [new SummaryItem("Search", Ms(3000))], [], "gemma", T0);
        await storage.SaveSummaryAsync(summary, Ct);
        await storage.SaveChaptersAsync(id, [new Chapter("Intro", TimeSpan.Zero), new Chapter("Models", Ms(3000))], Ct);
        await storage.SetSpeakerNameAsync(id, Speaker.Others, "Anna", Ct);

        await storage.ReplaceWordAsync(id, 1, 5, "Parakeet", Ct);

        Assert.Equal(summary.Overview, (await storage.GetSummaryAsync(id, Ct))!.Overview);
        Assert.Equal(2, (await storage.GetChaptersAsync(id, Ct)).Count);
        Assert.Equal("Anna", (await storage.GetSpeakerNamesAsync(id, Ct)).Others);
        var transcript = (await storage.GetTranscriptAsync(id, Ct))!;
        Assert.Equal("Okay, a quick run.", transcript.Segments[0].Text);
        Assert.Equal("parakeet", transcript.Engine);
        Assert.Equal("en", transcript.Language);
    }

    [Fact]
    public async Task KeepingTheWord_WritesTheSameText_AndOnlyTakesTheDoubtAway()
    {
        var storage = Open();
        var id = await WithTranscript(storage);

        await storage.ReplaceWordAsync(id, 1, 5, "parquet", Ct);

        var segment = (await storage.GetTranscriptAsync(id, Ct))!.Segments[1];
        Assert.Equal("Does it run on the  parquet model?", segment.Text);
        Assert.Null(segment.Words[5].Confidence);
        Assert.DoesNotContain(segment.Words, w => w.IsUncertain);
    }

    [Fact]
    public async Task TheSegmentIndex_IsThePositionInTheReadTranscript()
    {
        // Saved out of time order: GetTranscriptAsync sorts by start time, and ReplaceWordAsync counts the same way.
        var storage = Open();
        var id = (await storage.CreateSessionAsync("t", T0, Ct)).Id;
        await storage.SaveTranscriptAsync(
            new Transcript(id, [new TranscriptSegment(Speaker.Me, Ms(5000), Ms(6000), "later", [W("later", 5000, 0.2f)]), new TranscriptSegment(Speaker.Others, Ms(0), Ms(1000), "first", [W("first", 0, 0.2f)])], "e"),
            Ct);

        await storage.ReplaceWordAsync(id, 0, 0, "earlier", Ct);

        var segments = (await storage.GetTranscriptAsync(id, Ct))!.Segments;
        Assert.Equal(["earlier", "later"], segments.Select(s => s.Text));
    }

    [Fact]
    public async Task ReplaceWord_RejectsAnUnknownSessionAndIndexesOutOfRange()
    {
        var storage = Open();
        var id = await WithTranscript(storage);
        var other = (await storage.CreateSessionAsync("no transcript", T0, Ct)).Id;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await storage.ReplaceWordAsync(SessionId.New(), 0, 0, "x", Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await storage.ReplaceWordAsync(other, 0, 0, "x", Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await storage.ReplaceWordAsync(id, 2, 0, "x", Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await storage.ReplaceWordAsync(id, 1, 7, "x", Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await storage.ReplaceWordAsync(id, -1, 0, "x", Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await storage.ReplaceWordAsync(id, 0, -1, "x", Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await storage.ReplaceWordAsync(id, 1, 5, "  ", Ct));

        // Nothing changed.
        Assert.Equal("Does it run on the  parquet model?", (await storage.GetTranscriptAsync(id, Ct))!.Segments[1].Text);
    }

    [Theory]
    [InlineData("a b c", 1, "X", "a X c")]
    [InlineData("a  b,  c", 1, "X", "a  X,  c")]
    [InlineData("b b b", 2, "X", "b b X")]
    [InlineData("hello there", 1, "you", "hello you")]
    public void ReplaceWordInText_KeepsTheRestOfTheText(string text, int index, string replacement, string expected)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select((w, i) => W(w, i * 100)).ToArray();
        if (text == "a  b,  c")
        {
            words = [W("a", 0), W("b", 100), W("c", 200)];
        }

        Assert.Equal(expected, SqliteStorage.ReplaceWordInText(text, words, index, replacement));
    }

    [Fact]
    public void ReplaceWordInText_RebuildsFromTheWords_WhenTheTextDoesNotSpellThem()
    {
        TranscriptWord[] words = [W("one", 0), W("two", 100), W("three", 200)];

        Assert.Equal("one 2 three", SqliteStorage.ReplaceWordInText("1 2 3", words, 1, "2"));
    }

    // Vocabulary ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheVocabulary_IsEmpty_ThenNewestFirst()
    {
        var storage = Open();

        Assert.Empty(await storage.GetVocabularyAsync(Ct));

        await storage.AddVocabularyWordAsync("Parakeet", Ct);
        await storage.AddVocabularyWordAsync("Gemma", Ct);
        await storage.AddVocabularyWordAsync("sherpa-onnx", Ct);

        Assert.Equal(["sherpa-onnx", "Gemma", "Parakeet"], await storage.GetVocabularyAsync(Ct));
    }

    [Fact]
    public async Task ARepeat_InAnyCase_MovesToTheFront_WithTheNewSpelling()
    {
        var storage = Open();
        await storage.AddVocabularyWordAsync("parakeet", Ct);
        await storage.AddVocabularyWordAsync("Gemma", Ct);
        await storage.AddVocabularyWordAsync("Київ", Ct);

        await storage.AddVocabularyWordAsync("  Parakeet ", Ct);
        await storage.AddVocabularyWordAsync("КИЇВ", Ct);

        Assert.Equal(["КИЇВ", "Parakeet", "Gemma"], await storage.GetVocabularyAsync(Ct));
    }

    [Fact]
    public async Task TheVocabulary_Persists_AndIsNotTiedToASession()
    {
        var first = Open();
        var id = await WithTranscript(first);
        await first.AddVocabularyWordAsync("Parakeet", Ct);
        await first.DeleteSessionAsync(id, Ct);
        first.Dispose();

        var second = Open();
        Assert.Equal(["Parakeet"], await second.GetVocabularyAsync(Ct));
    }

    [Fact]
    public async Task ABlankWord_IsRejected_AndALongOneIsCut()
    {
        var storage = Open();

        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await storage.AddVocabularyWordAsync("   ", Ct));
        await storage.AddVocabularyWordAsync(new string('x', 300), Ct);

        Assert.Equal(SqliteStorage.MaxVocabularyWordLength, Assert.Single(await storage.GetVocabularyAsync(Ct)).Length);
    }

    [Fact]
    public async Task Migration10_CreatesTheVocabularyTable()
    {
        var storage = Open();
        await storage.GetVocabularyAsync(Ct);

        using var connection = new SqliteConnection($"Data Source={storage.Paths.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'vocabulary'";
        Assert.Equal(1L, command.ExecuteScalar());
        Assert.Equal(10, SchemaMigrator.GetVersion(connection));
        SqliteConnection.ClearPool(connection);
    }

    [Fact]
    public void ADatabaseOfSchema9_GainsTheVocabularyTable_AndKeepsItsSessions()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        SchemaMigrator.Apply(connection, [.. SchemaMigrator.Migrations.Take(9)]);
        Assert.Equal(9, SchemaMigrator.GetVersion(connection));
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO sessions (id, title, started_at, duration_ms, state, has_mic, has_system, screen_segments, folder) " +
                "VALUES ('a', 'kept', 0, 0, 1, 0, 0, '[]', 'f')";
            insert.ExecuteNonQuery();
        }

        Assert.Equal(10, SchemaMigrator.Apply(connection, SchemaMigrator.Migrations));

        using var check = connection.CreateCommand();
        check.CommandText = "SELECT (SELECT count(*) FROM sessions), (SELECT count(*) FROM vocabulary), (SELECT count(*) FROM sqlite_master WHERE name = 'vocabulary')";
        using var reader = check.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }
}
