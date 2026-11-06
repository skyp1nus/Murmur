using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;
using Murmur.Intelligence.Suggestions;
using Murmur.Intelligence.Tests.Fakes;

namespace Murmur.Intelligence.Tests;

/// <summary>#58: the local model's alternatives for an uncertain word.</summary>
public sealed class WordSuggesterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SessionId Id = SessionId.New();

    private static Transcript Transcript(string language = "en")
    {
        static TranscriptWord W(string text, double start, float? confidence = null) =>
            new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(start + 0.3)) { Confidence = confidence };

        return new Transcript(
            Id,
            [
                new TranscriptSegment(Speaker.Me, TimeSpan.Zero, TimeSpan.FromSeconds(2), "Okay.", [W("Okay.", 0)]),
                new TranscriptSegment(Speaker.Others, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), "Does it run on the parquet model?", [W("Does", 3), W("it", 3.3), W("run", 3.6), W("on", 3.9), W("the", 4.2), W("parquet", 4.5, 0.31f), W("model?", 4.8)]),
            ],
            "parakeet",
            language);
    }

    private static (WordSuggester Suggester, FakeLlm Llm) Create(string answer)
    {
        var llm = new FakeLlm();
        llm.Chat.Pieces = [answer];
        return (new WordSuggester(llm), llm);
    }

    [Fact]
    public async Task AsksWithTheSentenceAndTheWord_AndReadsOnePerLine()
    {
        var (suggester, llm) = Create("Parakeet\nparakeet model\nParquet");

        var suggestions = await suggester.SuggestAsync(Transcript(), 1, 5, Ct);

        Assert.Equal(["Parakeet", "parakeet model"], suggestions); // "Parquet" is the word itself (any case)
        var prompt = llm.Chat.LastMessages[^1].Text;
        Assert.Contains("Does it run on the [[parquet]] model?", prompt, StringComparison.Ordinal);
        Assert.Contains("Uncertain word: parquet", prompt, StringComparison.Ordinal);
        Assert.Contains("Language: English", prompt, StringComparison.Ordinal);
        Assert.Equal(ChatRole.System, llm.Chat.LastMessages[0].Role);
        Assert.NotNull(llm.Chat.LastOptions?.MaxOutputTokens);
    }

    [Fact]
    public async Task WithoutTheModels_OrWhileRecording_TheListIsEmpty()
    {
        var (suggester, llm) = Create("Parakeet");
        llm.Status = LlmStatus.NotInstalled;
        Assert.Empty(await suggester.SuggestAsync(Transcript(), 1, 5, Ct));
        Assert.Empty(llm.Chat.LastMessages);

        llm.Status = LlmStatus.Unloaded;
        llm.Paused = true;
        Assert.Empty(await suggester.SuggestAsync(Transcript(), 1, 5, Ct));
    }

    [Fact]
    public async Task OutOfRangeIndexes_Throw()
    {
        var (suggester, _) = Create("Parakeet");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await suggester.SuggestAsync(Transcript(), 2, 0, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await suggester.SuggestAsync(Transcript(), 1, 7, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await suggester.SuggestAsync(Transcript(), -1, 0, Ct));
    }

    [Theory]
    [InlineData("- Parakeet\n- Paraquat\n- parakeets\n- Parkit", new[] { "Parakeet", "Paraquat", "parakeets" })]
    [InlineData("1. **Parakeet**\n2) \"Paraquat\"", new[] { "Parakeet", "Paraquat" })]
    [InlineData("Parakeet, Paraquat; parakeets", new[] { "Parakeet", "Paraquat", "parakeets" })]
    [InlineData("Corrections:\nparquet → Parakeet\nParaquat (a herbicide)", new[] { "Parakeet", "Paraquat" })]
    [InlineData("Answer: «Parakeet».", new[] { "Parakeet" })]
    [InlineData("**Suggestions:** Parakeet, Paraquat", new[] { "Parakeet", "Paraquat" })]
    [InlineData("Parakeet: a speech model\nParaquat: a herbicide", new[] { "Parakeet", "Paraquat" })]
    [InlineData("Варіанти: Паракіт", new[] { "Паракіт" })]
    [InlineData("Parakeet\nparakeet\nPARAKEET", new[] { "Parakeet" })]
    [InlineData("This is a whole sentence about something else entirely\nParakeet", new[] { "Parakeet" })]
    [InlineData("", new string[0])]
    [InlineData("parquet", new string[0])]
    public void Parse_IsLenient_AndKeepsOnlyShortDistinctFixes(string answer, string[] expected) =>
        Assert.Equal(expected, WordSuggester.Parse(answer, "parquet"));

    [Fact]
    public void Core_DropsThePunctuationAroundTheWord()
    {
        Assert.Equal("model", WordSuggester.Core("model?"));
        Assert.Equal("Київ", WordSuggester.Core("«Київ»,"));
        Assert.Equal(string.Empty, WordSuggester.Core("…"));
    }

    [Fact]
    public async Task APunctuationOnlyWord_IsNotAsked()
    {
        var (suggester, llm) = Create("Parakeet");
        var transcript = new Transcript(Id, [new TranscriptSegment(Speaker.Me, TimeSpan.Zero, TimeSpan.FromSeconds(1), "—", [new TranscriptWord("—", TimeSpan.Zero, TimeSpan.FromSeconds(1))])], "e");

        Assert.Empty(await suggester.SuggestAsync(transcript, 0, 0, Ct));
        Assert.Empty(llm.Chat.LastMessages);
    }

    [Fact]
    public async Task UkrainianTranscript_SaysSoInThePrompt()
    {
        var (suggester, llm) = Create("Паракіт");

        Assert.Equal(["Паракіт"], await suggester.SuggestAsync(Transcript("uk"), 1, 5, Ct));
        Assert.Contains("Language: Ukrainian", llm.Chat.LastMessages[^1].Text, StringComparison.Ordinal);
    }
}
