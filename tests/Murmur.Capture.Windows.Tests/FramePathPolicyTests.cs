using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>Choice of the encoder input and the diagnostics around it (pure, any OS).</summary>
public sealed class FramePathPolicyTests
{
    [Theory]
    [InlineData(true, false, 1, true)]
    [InlineData(true, false, 3, true)]
    [InlineData(true, false, 0, false)]  // a GPU without an H.264 encoder MFT
    [InlineData(false, false, 2, false)] // device created without D3D11_CREATE_DEVICE_VIDEO_SUPPORT
    [InlineData(true, true, 2, false)]   // WARP / Basic Render Driver (windows-latest)
    [InlineData(false, true, 0, false)]
    public void Choose_ZeroCopy_OnlyForAHardwareEncoderOnARealGpu(bool videoSupport, bool softwareRenderer, int encoders, bool zeroCopy) =>
        Assert.Equal(zeroCopy ? EncoderInput.Gpu : EncoderInput.Cpu, FramePathPolicy.Choose(videoSupport, softwareRenderer, encoders));

    [Theory]
    [InlineData(true, false, 1, "GPU zero-copy (1 hardware H.264 encoder MFT)")]
    [InlineData(true, false, 2, "GPU zero-copy (2 hardware H.264 encoder MFTs)")]
    [InlineData(true, true, 0, "CPU readback: software renderer (WARP / Microsoft Basic Render Driver), no GPU encoder")]
    [InlineData(false, false, 1, "CPU readback: the D3D11 device has no video support")]
    [InlineData(true, false, 0, "CPU readback: no hardware H.264 encoder MFT")]
    public void Describe_SaysWhy(bool videoSupport, bool softwareRenderer, int encoders, string expected) =>
        Assert.Equal(expected, FramePathPolicy.Describe(videoSupport, softwareRenderer, encoders));

    [Fact]
    public void EveryFrameStep_NamesAnApi()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in Enum.GetValues<FrameStep>())
        {
            var name = FrameSteps.Describe(step);
            if (step == FrameStep.None)
            {
                Assert.Equal("an unknown step", name);
                continue;
            }

            Assert.NotEqual("an unknown step", name);
            Assert.True(names.Add(name), $"{step} shares its name");
        }
    }

    [Fact]
    public void FaultText_NamesTheStep_WhenItMatchesTheHResult()
    {
        var failure = new FrameFailure(FrameStep.SurfaceAccess, ScreenConstants.ENoInterface);
        Assert.Equal(
            "CPU readback frame copy failed at QueryInterface(IDirect3DDxgiInterfaceAccess) on the capture surface: HRESULT 0x80004002",
            FrameCopyNaming.Fault(EncoderInput.Cpu, failure, ScreenConstants.ENoInterface));
        Assert.Equal("GPU frame copy failed: HRESULT 0x80004005", FrameCopyNaming.Fault(EncoderInput.Gpu, failure, ScreenConstants.EFail));
        Assert.Equal("GPU frame copy failed: HRESULT 0x80004002", FrameCopyNaming.Fault(EncoderInput.Gpu, default, ScreenConstants.ENoInterface));
        Assert.Equal("GPU zero-copy", FrameCopyNaming.Path(EncoderInput.Gpu));
        Assert.Equal("CPU readback", FrameCopyNaming.Path(EncoderInput.Cpu));
    }

    [Fact]
    public void InputSubtype_IsArgb32ForTextures_AndRgb32ForSystemMemory()
    {
        Assert.Equal(ScreenConstants.MfVideoFormatArgb32, H264SinkWriter.InputSubtype(EncoderInput.Gpu));
        Assert.Equal(ScreenConstants.MfVideoFormatRgb32, H264SinkWriter.InputSubtype(EncoderInput.Cpu));
    }

    [Theory]
    [InlineData(0x1414u, 0x8Cu, true)]
    [InlineData(0x1414u, 0x8Du, false)]
    [InlineData(0x10DEu, 0x8Cu, false)]
    [InlineData(0u, 0u, false)]
    public void BasicRenderDriver_IsRecognized(uint vendor, uint device, bool expected) =>
        Assert.Equal(expected, DeviceNaming.IsBasicRenderDriver(vendor, device));

    [Theory]
    [InlineData(false, 0xB100, true, "NVIDIA GeForce RTX 3060 ", false, true, "hardware D3D11 FL 11.1 on 'NVIDIA GeForce RTX 3060'")]
    [InlineData(false, 0xB100, true, "Microsoft Basic Render Driver", true, true, "hardware D3D11 FL 11.1 on 'Microsoft Basic Render Driver' (software renderer)")]
    [InlineData(true, 0xB000, false, null, true, true, "WARP D3D11 FL 11.0 without video support (software renderer)")]
    [InlineData(false, 0xA100, true, "", false, false, "hardware D3D11 FL 10.1 (no multithread protection)")]
    public void DeviceDescription_NamesDriverLevelAdapterAndFlags(bool warp, int level, bool video, string? adapter, bool software, bool multithread, string expected) =>
        Assert.Equal(expected, DeviceNaming.Describe(warp, level, video, adapter, software, multithread));
}
