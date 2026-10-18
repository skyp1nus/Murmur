using Murmur.Core.Sessions;

namespace Murmur.Core.Tests.Sessions;

public sealed class SessionFilesTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "murmur-tests", "session");

    [Fact]
    public void Directory_IsKeptAsGiven()
    {
        Assert.Equal(Root, new SessionFiles(Root).Directory);
    }

    [Fact]
    public void AudioAndPeakFiles_LiveInsideTheSessionDirectory()
    {
        var files = new SessionFiles(Root);

        Assert.Equal(Path.Combine(Root, "mic.wav"), files.MicrophoneAudio);
        Assert.Equal(Path.Combine(Root, "system.wav"), files.SystemAudio);
        Assert.Equal(Path.Combine(Root, "peaks.bin"), files.Peaks);
    }

    [Theory]
    [InlineData(0, "screen-000.mp4")]
    [InlineData(7, "screen-007.mp4")]
    [InlineData(42, "screen-042.mp4")]
    [InlineData(999, "screen-999.mp4")]
    [InlineData(1234, "screen-1234.mp4")]
    public void ScreenSegment_IsZeroPaddedToThreeDigits(int index, string expectedName)
    {
        var files = new SessionFiles(Root);

        Assert.Equal(Path.Combine(Root, expectedName), files.ScreenSegment(index));
    }

    [Fact]
    public void AllPaths_AreDistinct()
    {
        var files = new SessionFiles(Root);
        string[] paths = [files.MicrophoneAudio, files.SystemAudio, files.Peaks, files.ScreenSegment(0), files.ScreenSegment(1)];

        Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void RecordsWithTheSameDirectory_AreEqual()
    {
        Assert.Equal(new SessionFiles(Root), new SessionFiles(Root));
    }
}
