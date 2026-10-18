using Murmur.Core.Capture;

namespace Murmur.Core.Tests.Capture;

public sealed class ScreenCaptureOptionsTests
{
    [Fact]
    public void Defaults_Are1080p60_At12MbpsMean_OnThePrimaryDisplay()
    {
        // Watchable recordings (D-030): what Recorder asks every screen recorder for.
        var options = new ScreenCaptureOptions();

        Assert.Null(options.DisplayId);
        Assert.Equal(60, options.FrameRate);
        Assert.Equal(1080, options.MaxHeight);
        Assert.Equal(12_000, options.BitrateKbps);
        Assert.Equal(options with { DisplayId = @"\\.\DISPLAY2" }, new ScreenCaptureOptions(@"\\.\DISPLAY2"));
    }
}
