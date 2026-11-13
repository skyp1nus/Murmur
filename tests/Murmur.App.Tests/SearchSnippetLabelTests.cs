using Murmur.App.Localization;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

/// <summary>
/// The Library search snippet shows the speaker label in the UI language (#106): the chunks carry the model labels
/// <c>Я</c> / <c>Інші</c>, which are the words of the index and the prompt, not of the screen.
/// </summary>
public sealed class SearchSnippetLabelTests : IDisposable
{
    private static readonly TimeSpan Debounce = LibraryViewModel.SearchDebounce;

    private readonly FakeSessionStore _store = new();
    private readonly TestTime _time = new(UiHarness.Now);
    private readonly FakeAssistant _assistant = new();
    private readonly FakeSearchIndex _keyword = new();
    private IDisposable? _culture;

    public void Dispose() => _culture?.Dispose();

    private void Ui(string language) => _culture = UiCulture.Override(language);

    private async Task<string[]> SearchAsync(params string[] chunkTexts)
    {
        var record = Sessions.Make("Стендап", UiHarness.Now.AddHours(-2), TimeSpan.FromHours(1), state: SessionState.Ready);
        _store.Add(record);
        _assistant.SearchResult = [.. chunkTexts.Select((text, i) => Hits.Make(record.Id, 10 + i, text))];
        var vm = new LibraryViewModel(_store, _time, post: action => action(), keywordSearch: _keyword, assistant: _assistant);
        await vm.LoadAsync();

        vm.SearchText = "бюджет";
        _time.Advance(Debounce);
        await vm.SearchTask;

        return [.. vm.SearchResults.Select(r => r.Snippet)];
    }

    [Fact]
    public async Task EnglishUi_ShowsOthersAndYou()
    {
        Ui("en");

        var snippets = await SearchAsync("Інші: бюджет на квартал", "Я: бюджет згоден");

        Assert.Equal(["Others: бюджет на квартал", "You: бюджет згоден"], snippets);
    }

    [Fact]
    public async Task UkrainianUi_ShowsTheUkrainianLabels()
    {
        Ui("uk");

        var snippets = await SearchAsync("Інші: бюджет на квартал", "Я: бюджет згоден");

        Assert.Equal(["Інші: бюджет на квартал", "Я: бюджет згоден"], snippets);
    }

    [Fact]
    public async Task EnglishUi_ShowsEveryLabelOfAChunk_NotOnlyTheFirst()
    {
        Ui("en");

        var snippets = await SearchAsync("Я: який бюджет?\nІнші: бюджет на квартал\nЯ: дякую");

        Assert.Equal(["You: який бюджет? Others: бюджет на квартал You: дякую"], snippets);
    }

    [Fact]
    public async Task ACustomSpeakerName_IsShownAsItIs_NextToTheDefaultLabelOfTheOtherSpeaker()
    {
        Ui("en");

        // What the assistant hands over for a recording with a name for one speaker (it already relabels with the name).
        var snippets = await SearchAsync("Марек: бюджет на квартал\nYou: добре");

        Assert.Equal(["Марек: бюджет на квартал You: добре"], snippets);
    }

    [Fact]
    public async Task ASnippetThatStartsInTheMiddleOfALine_HasNoLabelAddedAndIsNotChanged()
    {
        Ui("en");

        var snippets = await SearchAsync("…бюджет на квартал, сказали Інші: так", "бюджет на квартал");

        Assert.Equal(["…бюджет на квартал, сказали Інші: так", "бюджет на квартал"], snippets);
    }

    [Fact]
    public void Localize_ChangesOnlyTheLabelsAtTheStartOfALine()
    {
        using var _ = UiCulture.Override("en");

        Assert.Equal("You: a\nOthers: b Інші: c\nOthers: d", SpeakerLabels.Localize("Я: a\nІнші: b Інші: c\nІнші: d"));
        Assert.Equal("Інші:no space", SpeakerLabels.Localize("Інші:no space"));
        Assert.Equal("Яблуко: смачне", SpeakerLabels.Localize("Яблуко: смачне"));
        Assert.Equal(string.Empty, SpeakerLabels.Localize(string.Empty));
    }

    [Fact]
    public void Localize_ReturnsTheSameInstance_WhenThereIsNothingToChange()
    {
        using var _ = UiCulture.Override("uk");
        var text = "Я: привіт\nІнші: добрий день";

        Assert.Same(text, SpeakerLabels.Localize(text));
    }

    [Fact]
    public void TheModelLabels_AreTheOnesTheIndexWrites()
    {
        // Murmur.Intelligence TranscriptChunker.MeLabel / OthersLabel (pinned there by TranscriptChunkerTests).
        Assert.Equal("Я", SpeakerLabels.ModelMeLabel);
        Assert.Equal("Інші", SpeakerLabels.ModelOthersLabel);
    }
}
