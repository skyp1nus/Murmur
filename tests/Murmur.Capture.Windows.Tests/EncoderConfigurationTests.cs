using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// The 1080p60 encoder configuration (pure, any OS): per-path frame rate and bit rate, rate control parameters and
/// their diagnostics, the VARIANT the encoder reads back through, and the sample pool sizing.
/// </summary>
public sealed unsafe class EncoderConfigurationTests
{
    private static readonly PixelSize Size = new(1920, 1080);

    [Fact]
    public void Settings_GpuPath_EncodesWhatWasAskedFor()
    {
        var requested = new H264EncoderSettings(Size, 60, 12_000);
        Assert.Equal(requested, FramePathPolicy.Settings(EncoderInput.Gpu, requested));
    }

    [Theory]
    [InlineData(60, 12_000, 30, 6_000)] // the default on a machine without a GPU encoder: 1080p30, same bits per frame
    [InlineData(120, 12_000, 30, 3_000)]
    [InlineData(31, 3_100, 30, 3_000)]
    [InlineData(30, 12_000, 30, 12_000)] // at or below the cap: unchanged
    [InlineData(15, 4_000, 15, 4_000)]
    [InlineData(240, 1, 30, 1)] // never below 1 kbps
    public void Settings_CpuReadback_CapsTheFrameRate_AndScalesTheBitrate(int fps, int kbps, int expectedFps, int expectedKbps)
    {
        var settings = FramePathPolicy.Settings(EncoderInput.Cpu, new H264EncoderSettings(Size, fps, kbps, RateControl.Cbr));
        Assert.Equal((expectedFps, expectedKbps), (settings.FrameRate, settings.BitrateKbps));
        Assert.Equal(Size, settings.FrameSize);
        Assert.Equal(RateControl.Cbr, settings.RateControl);
        Assert.Equal(30, FramePathPolicy.CpuMaxFrameRate);
    }

    [Fact]
    public void EncoderSettings_DefaultToPeakConstrainedVbr_WithTwiceTheMeanAsPeak()
    {
        var settings = new H264EncoderSettings(Size, 60, 12_000);
        Assert.Equal(RateControl.PeakConstrainedVbr, settings.RateControl);
        Assert.Equal(ScreenConstants.RateControlPeakConstrainedVbr, settings.RateControlMode);
        Assert.Equal(12_000_000u, settings.MeanBitrate);
        Assert.Equal(24_000_000u, settings.PeakBitrate);
        Assert.Equal(120u, settings.GopSize); // 2 s at 60 fps
        Assert.Equal(ScreenConstants.RateControlCbr, (settings with { RateControl = RateControl.Cbr }).RateControlMode);
    }

    [Fact]
    public void EncoderSettings_BitratesSaturate_InsteadOfOverflowing()
    {
        var settings = new H264EncoderSettings(Size, 60, int.MaxValue);
        Assert.Equal(uint.MaxValue, settings.MeanBitrate);
        Assert.Equal(uint.MaxValue, settings.PeakBitrate);
        Assert.Equal(uint.MaxValue, new H264EncoderSettings(Size, 60, 3_000_000).PeakBitrate);
    }

    [Theory]
    [InlineData(0u, "CBR")]
    [InlineData(1u, "peak-constrained VBR")]
    [InlineData(2u, "unconstrained VBR")]
    [InlineData(3u, "quality VBR")]
    [InlineData(6u, "rate control mode 6")]
    public void RateControlNaming_NamesTheModes(uint mode, string expected) =>
        Assert.Equal(expected, RateControlNaming.Mode(mode));

    [Fact]
    public void RateControlNaming_SaysWhatTheEncoderReported()
    {
        var vbr = new H264EncoderSettings(Size, 60, 12_000);
        Assert.Equal(
            "peak-constrained VBR, mean 12000 kbps, peak 24000 kbps",
            RateControlNaming.Describe(vbr, new RateControlReadback(1, 12_000_000, 24_000_000), null));

        // An encoder that accepted the parameters but kept CBR says so.
        Assert.Equal("CBR, 12000 kbps", RateControlNaming.Describe(vbr, new RateControlReadback(0, 12_000_000, null), null));

        // A peak that cannot be read: mean only; a mean that cannot be read: the requested one.
        Assert.Equal("peak-constrained VBR, mean 12000 kbps", RateControlNaming.Describe(vbr, new RateControlReadback(1, null, null), null));
        Assert.Equal("quality VBR, mean 8000 kbps", RateControlNaming.Describe(vbr, new RateControlReadback(3, 8_000_000, 24_000_000), null));

        Assert.Equal(
            "requested peak-constrained VBR, mean 12000 kbps (the encoder does not report its rate control)",
            RateControlNaming.Describe(vbr, RateControlReadback.Unknown, null));
        Assert.Equal(
            "CBR, 12000 kbps (peak-constrained VBR was rejected: IMFSinkWriter::SetInputMediaType failed)",
            RateControlNaming.Describe(vbr with { RateControl = RateControl.Cbr }, new RateControlReadback(0, 12_000_000, 12_000_000), "IMFSinkWriter::SetInputMediaType failed"));
    }

    [Fact]
    public void RateControlNaming_IsVbr_OnlyForAReportedVbrMode()
    {
        Assert.True(RateControlNaming.IsVbr(new RateControlReadback(1, null, null)));
        Assert.True(RateControlNaming.IsVbr(new RateControlReadback(3, null, null)));
        Assert.False(RateControlNaming.IsVbr(new RateControlReadback(0, 1, 1)));
        Assert.False(RateControlNaming.IsVbr(RateControlReadback.Unknown));
    }

    [Fact]
    public void Variant_HasTheVariantLayout_AndReadsScalars()
    {
        Assert.Equal(24, sizeof(Variant)); // VARIANT on 64-bit; 32-bit VARIANTs are 16 bytes and fit
        var value = Variant.FromUInt32(12_000_000);
        var bytes = new ReadOnlySpan<byte>(&value, sizeof(Variant));
        Assert.Equal(ScreenConstants.VtUI4, BitConverter.ToUInt16(bytes));
        Assert.Equal(12_000_000u, BitConverter.ToUInt32(bytes[8..]));
        Assert.Equal(12_000_000u, value.AsUInt32());

        Assert.Equal(7u, new Variant { Vt = ScreenConstants.VtI4, UI4 = 7 }.AsUInt32());
        Assert.Equal(1u, new Variant { Vt = ScreenConstants.VtUInt, UI4 = 1 }.AsUInt32());
        Assert.Equal(0xFFFFu, new Variant { Vt = ScreenConstants.VtBool, UI4 = 0xFFFF_FFFF }.AsUInt32()); // VARIANT_TRUE is a 16-bit -1
        Assert.Equal(5u, new Variant { Vt = ScreenConstants.VtUI8, UI8 = 5 }.AsUInt32());
        Assert.Null(new Variant { Vt = ScreenConstants.VtUI8, UI8 = ulong.MaxValue }.AsUInt32());
        Assert.Null(default(Variant).AsUInt32()); // VT_EMPTY
    }

    [Fact]
    public void CodecApiGuids_MatchCodecApiHeader()
    {
        Assert.Equal(new Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA"), ScreenConstants.IidICodecAPI);
        Assert.Equal(new Guid("1C0608E9-370C-4710-8A58-CB6181C42423"), ScreenConstants.CodecApiAVEncCommonRateControlMode);
        Assert.Equal(new Guid("F7222374-2144-4815-B550-A37F8E12EE52"), ScreenConstants.CodecApiAVEncCommonMeanBitRate);
        Assert.Equal(new Guid("9651EAE4-39B9-4EBF-85EF-D7F444EC7465"), ScreenConstants.CodecApiAVEncCommonMaxBitRate);
        Assert.Equal(new Guid("FCBF57A3-7EA5-4B0C-9644-69B40C39C391"), ScreenConstants.CodecApiAVEncCommonQuality);
        Assert.Equal(new Guid("398C1B98-8353-475A-9EF2-8F265D260345"), ScreenConstants.CodecApiAVEncVideoForceKeyFrame);
        Assert.Equal((0u, 1u, 2u, 3u), (ScreenConstants.RateControlCbr, ScreenConstants.RateControlPeakConstrainedVbr, ScreenConstants.RateControlUnconstrainedVbr, ScreenConstants.RateControlQuality));
        Assert.Equal((3, 11, 19, 21, 22, 23), ((int)ScreenConstants.VtI4, (int)ScreenConstants.VtBool, (int)ScreenConstants.VtUI4, (int)ScreenConstants.VtUI8, (int)ScreenConstants.VtInt, (int)ScreenConstants.VtUInt));
    }

    [Theory]
    [InlineData(0, 0, 24u, 24)]
    [InlineData(2, 4, 24u, 22)]  // two free, 20 not created yet
    [InlineData(0, 24, 24u, 0)]  // empty for sure
    [InlineData(3, 24, 24u, 3)]
    [InlineData(-1, 30, 24u, 0)] // nonsense never goes negative
    public void SamplePoolProbe_Available_CountsFreeAndNotYetCreatedSamples(int free, int handedOut, uint maximum, int expected) =>
        Assert.Equal(expected, SamplePoolProbe.Available(free, handedOut, maximum));

    [Fact]
    public void GpuSamplePool_CoversTheQueue_And400MsAt60Fps()
    {
        // Queue + held frame + the one being filled + what the hardware encoder holds; 400 ms of frames at 60 fps.
        Assert.True(WgcScreenRecorder.MaximumSamples >= WgcScreenRecorder.QueueCapacity + 2 + 4);
        Assert.True(WgcScreenRecorder.MaximumSamples >= 60 * 2 / 5);
        Assert.True(WgcScreenRecorder.InitialSamples <= WgcScreenRecorder.MaximumSamples);
        Assert.True(WgcScreenRecorder.MaximumSamples <= 32, "GpuFrameCopier caches at most 32 samples");
        Assert.True(WgcScreenRecorder.QueueCapacity * 1000 / 60 >= 100, "the frame queue should absorb 100 ms of encoder stalls at 60 fps");
    }
}

/// <summary>Sizing of the CPU readback sample pool against the frame queue (pure).</summary>
public sealed class CpuSamplePoolSizingTests
{
    [Fact]
    public async Task CpuSamplePool_OutlastsAFullQueue()
    {
        // A stalled encoder must fill the queue before the pool runs dry: queue + held frame + the one being filled +
        // what the encoder pipeline holds. The pool used to be as large as the queue alone (8 and 8).
        Assert.Equal(
            WgcScreenRecorder.QueueCapacity + 1 + 1 + WgcScreenRecorder.CpuEncoderHeldSamples,
            WgcScreenRecorder.MaximumCpuSamples);
        Assert.True(WgcScreenRecorder.CpuEncoderHeldSamples >= 1);
        Assert.InRange(WgcScreenRecorder.InitialCpuSamples, 3, WgcScreenRecorder.MaximumCpuSamples);
        await using var recorder = new WgcScreenRecorder(new Murmur.Core.Capture.ScreenCaptureOptions());
        Assert.Equal(WgcScreenRecorder.MaximumCpuSamples, recorder.CpuSampleLimit);
    }
}
