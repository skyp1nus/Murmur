using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests;

/// <summary>#58 next to #59: a recording that has bookmarks and doubtful words shows both.</summary>
public sealed class UncertainWordsWithBookmarksTests : IDisposable
{
    private readonly SessionFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ASessionWithBookmarks_StillShowsItsPills_AndItsUncertainWords()
    {
        var transcript = new Transcript(
            _fixture.Record.Id,
            [
                new TranscriptSegment(Speaker.Me, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12), "Okay, a quick run.", [new("Okay,", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10.4)) { Confidence = 0.9f }, new("a", TimeSpan.FromSeconds(10.5), TimeSpan.FromSeconds(10.9)) { Confidence = 0.2f }]),
                new TranscriptSegment(Speaker.Others, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(23), "Does it run", [new("Does", TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20.4)) { Confidence = 0.9f }, new("it", TimeSpan.FromSeconds(20.5), TimeSpan.FromSeconds(20.9)) { Confidence = 0.9f }]),
            ],
            "fake",
            "en");
        _fixture.UseTranscript(transcript);
        var bookmarks = new FakeBookmarkStore();
        bookmarks.Put(_fixture.Record.Id, (13, "After the first line"));
        var vm = new SessionViewModel(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(),
            services: new SessionServices(Bookmarks: bookmarks, Vocabulary: new FakeVocabularyStore(), WordSuggester: new FakeWordSuggester()));

        await vm.LoadAsync();

        Assert.Equal(["After the first line"], vm.Segments[0].MarksAfter.Select(b => b.Note));
        Assert.Equal([false, true], vm.Segments[0].UncertainWords!);
        Assert.Null(vm.Segments[1].UncertainWords);
    }
}
