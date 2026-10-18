using Murmur.Capture.Windows.Screen;

namespace Murmur.Capture.Windows.Tests;

public sealed class ScreenNamingTests
{
    [Theory]
    [InlineData(@"\\.\DISPLAY1", "Generic PnP Monitor", "Display 1 (Generic PnP Monitor)")]
    [InlineData(@"\\.\DISPLAY12", " DELL U2720Q ", "Display 12 (DELL U2720Q)")]
    [InlineData(@"\\.\DISPLAY2", "", "Display 2")]
    [InlineData(@"\\.\DISPLAY3", null, "Display 3")]
    [InlineData(@"\\.\DISPLAY0", null, "Display 0")]
    [InlineData(@"\\.\WINDISC", "Virtual", "WINDISC (Virtual)")]
    public void DisplayName_IsHumanReadable(string device, string? monitor, string expected) =>
        Assert.Equal(expected, DisplayNaming.Format(device, monitor));

    [Theory]
    [InlineData(true, "NVIDIA H.264 Encoder MFT", true, "Media Foundation H.264 (hardware: NVIDIA H.264 Encoder MFT, D3D11 input)")]
    [InlineData(false, "H264 Encoder MFT", true, "Media Foundation H.264 (software: H264 Encoder MFT, D3D11 input)")]
    [InlineData(false, null, false, "Media Foundation H.264 (software, CPU readback)")]
    [InlineData(null, "  ", true, "Media Foundation H.264 (hardware transforms allowed, D3D11 input)")]
    public void EncoderName_SaysHardwareOrSoftware(bool? hardware, string? name, bool gpuInput, string expected) =>
        Assert.Equal(expected, EncoderNaming.Describe(hardware, name, gpuInput));

    [Fact]
    public void GopSize_IsTwoSeconds() =>
        Assert.Equal(60u, new H264EncoderSettings(new PixelSize(1920, 1080), 30, 6000).GopSize);
}
