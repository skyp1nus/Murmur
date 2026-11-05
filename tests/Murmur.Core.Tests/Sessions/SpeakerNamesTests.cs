using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Core.Tests.Sessions;

public sealed class SpeakerNamesTests
{
    [Fact]
    public void TheDefault_HasNoNames()
    {
        Assert.Null(SpeakerNames.Default.Me);
        Assert.Null(SpeakerNames.Default.Others);
        Assert.Null(SpeakerNames.Default[Speaker.Me]);
        Assert.Null(SpeakerNames.Default[Speaker.Others]);
    }

    [Fact]
    public void TheIndexer_PicksTheNameOfTheSpeaker()
    {
        var names = new SpeakerNames("Олена", "Anna K.");

        Assert.Equal("Олена", names[Speaker.Me]);
        Assert.Equal("Anna K.", names[Speaker.Others]);
        Assert.Equal(new SpeakerNames("Олена", "Anna K."), names);
    }
}
