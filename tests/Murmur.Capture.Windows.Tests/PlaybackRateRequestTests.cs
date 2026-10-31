using Murmur.Capture.Windows.Video;

namespace Murmur.Capture.Windows.Tests;

/// <summary>A refused playback rate must not fail the segment (the view gives a failed segment up), only fall back to 1x.</summary>
public sealed class PlaybackRateRequestTests
{
    private const int UnsupportedRate = unchecked((int)0xC00D36B3); // MF_E_UNSUPPORTED_RATE

    [Fact]
    public void AnAcceptedRate_SetsBoth_AndReportsSuccess()
    {
        var calls = new List<string>();

        var hr = PlaybackRateRequest.Apply(1.5, r => Record(calls, "default", r, 0), r => Record(calls, "rate", r, 0));

        Assert.Equal(0, hr);
        Assert.Equal(["default:1.5", "rate:1.5"], calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARefusedRate_FallsBackTo1x_ReturnsTheHresult_AndDoesNotThrow(bool refusedByTheDefault)
    {
        var calls = new List<string>();

        var hr = PlaybackRateRequest.Apply(
            2,
            r => Record(calls, "default", r, refusedByTheDefault && r != 1 ? UnsupportedRate : 0),
            r => Record(calls, "rate", r, r != 1 ? UnsupportedRate : 0));

        Assert.Equal(UnsupportedRate, hr);
        Assert.Equal("default:1", calls[^2]);
        Assert.Equal("rate:1", calls[^1]); // back in real time, so the audio clock's seeks keep the picture aligned
    }

    [Fact]
    public void Rate1_IsNeverRetried()
    {
        var calls = new List<string>();

        var hr = PlaybackRateRequest.Apply(1, r => Record(calls, "default", r, -1), r => Record(calls, "rate", r, 0));

        Assert.True(hr < 0);
        Assert.Equal(["default:1"], calls);
    }

    private static int Record(List<string> calls, string name, double rate, int result)
    {
        calls.Add(FormattableString.Invariant($"{name}:{rate}"));
        return result;
    }
}
