using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;
using Murmur.Intelligence.Rag;
using Murmur.Intelligence.Tests.Fakes;

namespace Murmur.Intelligence.Tests;

/// <summary>The names the user gave the speakers: in the chunks that are indexed, in search hits, and in the prompt of Ask AI.</summary>
public sealed class SpeakerNameLabelTests
{
    private static readonly SessionId S1 = new(Guid.Parse("0190a000-0000-7000-8000-000000000001"));
    private static readonly SessionId S2 = new(Guid.Parse("0190a000-0000-7000-8000-000000000002"));

    private const string DefaultChunk = "Я: Привіт\nІнші: Добрий день\nЯ: Починаємо";

    private readonly FakeStorage _storage = new();
    private readonly FakeLlm _llm = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SearchHit Hit(SessionId session, double start, double end, SearchSource source, string text) =>
        new(new TextChunk(session, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text), 1, source);

    private static Transcript Talk(SessionId session) => new(
        session,
        [
            TranscriptChunkerTests.Seg(0, 10, "Привіт", Speaker.Me),
            TranscriptChunkerTests.Seg(10, 20, "Добрий день", Speaker.Others),
            TranscriptChunkerTests.Seg(20, 30, "Починаємо", Speaker.Me),
        ],
        "test");

    private Assistant Create() => new(_llm, _storage);

    private static async Task<List<AssistantUpdate>> Collect(IAsyncEnumerable<AssistantUpdate> updates)
    {
        var list = new List<AssistantUpdate>();
        await foreach (var update in updates)
        {
            list.Add(update);
        }

        return list;
    }

    // The chunker -----------------------------------------------------------------------------------------------

    [Fact]
    public void TheChunker_LabelsTheLinesWithTheNames()
    {
        var chunk = Assert.Single(new TranscriptChunker().Chunk(Talk(S1), new SpeakerNames("Олена", "Marek W.")));

        Assert.Equal("Олена: Привіт\nMarek W.: Добрий день\nОлена: Починаємо", chunk.Text);
    }

    [Fact]
    public void ASpeakerWithoutAName_KeepsTheDefaultLabel()
    {
        var chunk = Assert.Single(new TranscriptChunker().Chunk(Talk(S1), new SpeakerNames(null, "Marek W.")));

        Assert.Equal("Я: Привіт\nMarek W.: Добрий день\nЯ: Починаємо", chunk.Text);
    }

    [Fact]
    public void WithoutNames_TheChunksAreTheOnesOfBefore()
    {
        var chunker = new TranscriptChunker();

        Assert.Equal(chunker.Chunk(Talk(S1)), chunker.Chunk(Talk(S1), SpeakerNames.Default));
        Assert.Equal(DefaultChunk, Assert.Single(chunker.Chunk(Talk(S1), null)).Text);
    }

    // Indexing --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Indexing_UsesTheNames_InTheChunksAndInTheEmbeddedText()
    {
        _storage.Transcripts[S1] = Talk(S1);
        _storage.SpeakerNames[S1] = new SpeakerNames("Олена", "Marek W.");

        await Create().IndexSessionAsync(S1, Ct);

        var (_, chunks, _) = Assert.Single(_storage.Replaced);
        Assert.Equal("Олена: Привіт\nMarek W.: Добрий день\nОлена: Починаємо", Assert.Single(chunks).Text);
        Assert.Contains("Marek W.: Добрий день", Assert.Single(Assert.Single(_llm.Embeddings.Batches)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Indexing_WithoutNames_KeepsTheDefaultLabels()
    {
        _storage.Transcripts[S1] = Talk(S1);

        await Create().IndexSessionAsync(S1, Ct);

        Assert.Equal(DefaultChunk, Assert.Single(Assert.Single(_storage.Replaced).Chunks).Text);
    }

    // Search ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Search_ShowsTheNames_InAChunkIndexedBeforeTheRename()
    {
        _storage.Transcripts[S1] = Talk(S1);
        _storage.SpeakerNames[S1] = new SpeakerNames("Олена", "Marek W.");
        _storage.VectorHits = [Hit(S1, 0, 30, SearchSource.Vector, DefaultChunk)];

        var hit = Assert.Single(await Create().SearchAsync("привіт", null, 5, Ct));

        Assert.Equal("Олена: Привіт\nMarek W.: Добрий день\nОлена: Починаємо", hit.Chunk.Text);
        Assert.Equal(TimeSpan.Zero, hit.Chunk.Start);
        Assert.Equal(TimeSpan.FromSeconds(30), hit.Chunk.End);
    }

    [Fact]
    public async Task Search_PutsTheSpeakersName_InFrontOfASegmentFoundByKeyword()
    {
        _storage.Transcripts[S1] = Talk(S1);
        _storage.SpeakerNames[S1] = new SpeakerNames(null, "Marek W.");
        _storage.KeywordHits = [Hit(S1, 10, 20, SearchSource.Keyword, "Добрий день")];
        _llm.Status = LlmStatus.NotInstalled;

        var hit = Assert.Single(await Create().SearchAsync("день", null, 5, Ct));

        Assert.Equal("Marek W.: Добрий день", hit.Chunk.Text);
        Assert.Equal(SearchSource.Keyword, hit.Source);
    }

    [Fact]
    public async Task Search_LabelsEverySegmentOfANamedSession_TheSpeakerWithoutANameWithTheDefaultLabel()
    {
        _storage.Transcripts[S1] = Talk(S1);
        _storage.SpeakerNames[S1] = new SpeakerNames(null, "Marek W.");
        _storage.KeywordHits = [Hit(S1, 0, 10, SearchSource.Keyword, "Привіт"), Hit(S1, 10, 20, SearchSource.Keyword, "Добрий день")];
        _llm.Status = LlmStatus.NotInstalled;

        var hits = await Create().SearchAsync("привіт", null, 5, Ct);

        Assert.Equal(["Marek W.: Добрий день", "Я: Привіт"], hits.Select(h => h.Chunk.Text).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Search_DoesNotReadTheTranscriptAgain_WhileTheNamesStayTheSame()
    {
        _storage.Transcripts[S1] = Talk(S1);
        _storage.SpeakerNames[S1] = new SpeakerNames("Олена", "Marek W.");
        _storage.VectorHits = [Hit(S1, 0, 30, SearchSource.Vector, DefaultChunk)];
        var assistant = Create();

        await assistant.SearchAsync("привіт", null, 5, Ct);
        await assistant.SearchAsync("день", null, 5, Ct);
        var third = Assert.Single(await assistant.SearchAsync("починаємо", null, 5, Ct));

        Assert.Equal(1, _storage.TranscriptReads);
        Assert.StartsWith("Олена: Привіт", third.Chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_FollowsARename_AndTheCacheIsDroppedWhenTheSessionIsIndexed()
    {
        _storage.Transcripts[S1] = Talk(S1);
        _storage.SpeakerNames[S1] = new SpeakerNames("Олена", null);
        _storage.VectorHits = [Hit(S1, 0, 30, SearchSource.Vector, DefaultChunk)];
        var assistant = Create();
        await assistant.SearchAsync("привіт", null, 5, Ct);

        _storage.SpeakerNames[S1] = new SpeakerNames("Ірина", null);
        var renamed = Assert.Single(await assistant.SearchAsync("привіт", null, 5, Ct));
        await assistant.IndexSessionAsync(S1, Ct);
        await assistant.SearchAsync("привіт", null, 5, Ct);

        Assert.StartsWith("Ірина: Привіт", renamed.Chunk.Text, StringComparison.Ordinal);
        Assert.Equal(1 + 1 + 1 + 1, _storage.TranscriptReads); // first search, the rename, the index run, the search after it
    }

    [Fact]
    public async Task Search_KeepsTheHitsAsTheyAre_WhenTheTranscriptCannotBeRead()
    {
        _storage.Transcripts[S1] = Talk(S1);
        _storage.SpeakerNames[S1] = new SpeakerNames("Олена", null);
        _storage.TranscriptFailure = new InvalidDataException("broken");
        _storage.VectorHits = [Hit(S1, 0, 30, SearchSource.Vector, DefaultChunk)];

        var hit = Assert.Single(await Create().SearchAsync("привіт", null, 5, Ct));

        Assert.Equal(DefaultChunk, hit.Chunk.Text);
    }

    [Fact]
    public async Task Search_InASessionWithoutNames_ReturnsTheHitsUntouched()
    {
        _storage.Transcripts[S1] = Talk(S1);
        _storage.VectorHits = [Hit(S1, 0, 30, SearchSource.Vector, DefaultChunk)];

        var hit = Assert.Single(await Create().SearchAsync("привіт", null, 5, Ct));

        Assert.Equal(DefaultChunk, hit.Chunk.Text);
    }

    [Fact]
    public async Task Search_NamesOnlyTheSessionThatHasThem()
    {
        _storage.Transcripts[S1] = Talk(S1);
        _storage.Transcripts[S2] = Talk(S2);
        _storage.SpeakerNames[S2] = new SpeakerNames("Олена", null);
        _storage.VectorHits = [Hit(S1, 0, 30, SearchSource.Vector, DefaultChunk), Hit(S2, 0, 30, SearchSource.Vector, DefaultChunk)];

        var hits = await Create().SearchAsync("привіт", null, 5, Ct);

        Assert.Equal(DefaultChunk, hits.Single(h => h.Chunk.Session == S1).Chunk.Text);
        Assert.Equal("Олена: Привіт\nІнші: Добрий день\nОлена: Починаємо", hits.Single(h => h.Chunk.Session == S2).Chunk.Text);
    }

    // Ask AI ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ask_ThePrompt_CarriesTheNames_InTheExcerptsAndInANoteAboutWhoTheyAre()
    {
        _storage.AddSession(S1, "Ретро");
        _storage.Transcripts[S1] = Talk(S1);
        _storage.SpeakerNames[S1] = new SpeakerNames("Олена", "Marek W.");
        _storage.VectorHits = [Hit(S1, 0, 30, SearchSource.Vector, DefaultChunk)];
        _llm.Chat.Pieces = ["ok"];

        await Collect(Create().AskAsync(new AskRequest("Що вирішили?"), Ct));

        var prompt = _llm.Chat.LastMessages[^1].Text;
        Assert.Contains("У записі «Ретро» «Олена» — користувач, «Marek W.» — інші учасники розмови.", prompt, StringComparison.Ordinal);
        Assert.Contains("Уривок 1 — «Ретро», [00:00–00:30]:\nОлена: Привіт\nMarek W.: Добрий день\nОлена: Починаємо", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Я:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Інші:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ask_TheCitationSnippet_HasTheNames()
    {
        _storage.AddSession(S1, "Ретро");
        _storage.Transcripts[S1] = Talk(S1);
        _storage.SpeakerNames[S1] = new SpeakerNames("Олена", "Marek W.");
        _storage.VectorHits = [Hit(S1, 0, 30, SearchSource.Vector, DefaultChunk)];
        _llm.Chat.Pieces = ["Привіталися [00:05]."];

        var final = (await Collect(Create().AskAsync(new AskRequest("q"), Ct)))[^1];

        Assert.Contains("Олена: Привіт", Assert.Single(final.Citations).Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ask_WithoutNames_HasNoNote_AndTheDefaultLabels()
    {
        _storage.AddSession(S1, "Ретро");
        _storage.Transcripts[S1] = Talk(S1);
        _storage.VectorHits = [Hit(S1, 0, 30, SearchSource.Vector, DefaultChunk)];
        _llm.Chat.Pieces = ["ok"];

        await Collect(Create().AskAsync(new AskRequest("q"), Ct));

        var prompt = _llm.Chat.LastMessages[^1].Text;
        Assert.DoesNotContain("У записі", prompt, StringComparison.Ordinal);
        Assert.Contains("Я: Привіт", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Quotes_InANameCannotCloseTheQuotation_AndTwoEqualNamesFallBackToTheDefaults()
    {
        var quoted = new TranscriptChunker().Chunk(Talk(S1), new SpeakerNames("A\" «B»", "Marek")).Single().Text;

        Assert.StartsWith("A' 'B': Привіт\nMarek: Добрий день", quoted, StringComparison.Ordinal);
        Assert.Equal(DefaultChunk, new TranscriptChunker().Chunk(Talk(S1), new SpeakerNames("Same", "same")).Single().Text);
        Assert.Equal("У записі «T» «A' 'B'» — користувач.", Assistant.SpeakerNote("T", new SpeakerNames("A\" «B»", null)));
        Assert.Null(Assistant.SpeakerNote("T", new SpeakerNames("Same", "SAME")));
    }

    [Fact]
    public void TheNote_NamesOnlyTheSpeakersThatHaveAName()
    {
        Assert.Null(Assistant.SpeakerNote("T", SpeakerNames.Default));
        Assert.Equal("У записі «T» «Олена» — користувач.", Assistant.SpeakerNote("T", new SpeakerNames("Олена", null)));
        Assert.Equal("У записі «T» «Anna» — інші учасники розмови.", Assistant.SpeakerNote("T", new SpeakerNames(null, "Anna")));
    }
}
