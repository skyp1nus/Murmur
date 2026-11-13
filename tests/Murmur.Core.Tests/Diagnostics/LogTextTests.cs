using Murmur.Core.Diagnostics;

namespace Murmur.Core.Tests.Diagnostics;

public sealed class LogTextTests
{
    [Theory]
    [InlineData(@"C:\Users\olena\AppData\Local\Murmur\Recordings\Budget review\mic.wav", "mic.wav")]
    [InlineData("/home/olena/.local/share/Murmur/Recordings/Budget review/system.wav", "system.wav")]
    [InlineData(@"C:\Users\olena\Recordings\", "Recordings")]
    [InlineData("mic.wav", "mic.wav")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void FileName_KeepsOnlyTheLastPart(string? path, string expected) => Assert.Equal(expected, LogText.FileName(path));

    [Fact]
    public void Error_IsTheTypeAndTheHResult_NeverTheMessage()
    {
        var error = new IOException("Could not open 'Budget review' for Olena", unchecked((int)0x80070020));

        Assert.Equal("IOException 0x80070020", LogText.Error(error));
        Assert.Equal("IOException 0x80070020", LogText.Error(new AggregateException(error)));
        Assert.StartsWith("AggregateException 0x", LogText.Error(new AggregateException(error, new InvalidOperationException())), StringComparison.Ordinal);
        Assert.Equal(string.Empty, LogText.Error(null));
    }

    [Theory]
    [InlineData(@"Could not find file 'C:\Users\olena\Recordings\Budget review\mic.wav'.", "Could not find file 'mic.wav'.")]
    [InlineData("Could not find a part of the path '/home/olena/Recordings/Budget review/screen-001.mp4'.", "Could not find a part of the path 'screen-001.mp4'.")]
    [InlineData(@"Access to C:\Users\olena\Recordings\Budget review\mic.wav is denied.", "Access to mic.wav is denied.")]
    [InlineData("loading /home/olena/Recordings/Budget review/mic.wav failed", "loading mic.wav failed")]
    [InlineData("The input string 'the launch moves to March' was not in a correct format.", "The input string '...' was not in a correct format.")]
    [InlineData("Can't open the device: 0x88890004", "Can't open the device: 0x88890004")]
    [InlineData("and/or the encoder failed", "and/or the encoder failed")]
    [InlineData("first line\nsecond\tline", "first line second line")]
    public void Scrub_ReducesPathsAndDropsQuotedValues(string text, string expected) => Assert.Equal(expected, LogText.Scrub(text));

    [Fact]
    public void Scrub_CapsTheLength()
    {
        var scrubbed = LogText.Scrub(new string('a', 1000));

        Assert.Equal(LogText.MaxDetail + 3, scrubbed.Length);
        Assert.EndsWith("...", scrubbed, StringComparison.Ordinal);
        Assert.Equal(string.Empty, LogText.Scrub(null));
    }

    [Fact]
    public void Tail_TakesTheLastTwoLines_Scrubbed()
    {
        const string stderr = "loading model\nusing 8 threads\n\nreading /home/olena/Recordings/Budget review/mic.wav\nerror: out of memory\n";

        Assert.Equal("reading mic.wav | error: out of memory", LogText.Tail(stderr));
        Assert.Equal("error: out of memory", LogText.Tail(stderr, 1));
        Assert.Equal(string.Empty, LogText.Tail("  \n "));
        Assert.Equal(string.Empty, LogText.Tail(null));
    }

    [Fact]
    public void NullAppLog_KeepsNothing_AndHasNoFolder()
    {
        var log = NullAppLog.Instance;

        log.Write(LogLevel.Error, "a", "b", "c");
        log.Crash(new InvalidOperationException(), "ui");
        log.Flush();
        log.MarkCrashSeen();

        Assert.Null(log.UnseenCrash);
        Assert.Equal(string.Empty, log.Directory);
        Assert.Equal(string.Empty, log.Snapshot());
    }
}
