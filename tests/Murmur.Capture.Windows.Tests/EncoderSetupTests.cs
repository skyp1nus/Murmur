using System.Diagnostics;
using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// When opening the encoder gives VBR up for CBR (<see cref="RateControlFallbackPolicy"/>), what a failed retry reports
/// (<see cref="EncoderSetupException"/>), and the VARIANT clean-up of the rate control read-back.
/// </summary>
public sealed unsafe class EncoderSetupTests
{
    [Theory]
    [InlineData(ScreenConstants.EInvalidArg)]
    [InlineData(ScreenConstants.ENotImpl)]
    [InlineData(ScreenConstants.MfEInvalidMediaType)]
    [InlineData(ScreenConstants.MfEInvalidType)]
    [InlineData(ScreenConstants.MfEAttributeNotFound)]
    [InlineData(ScreenConstants.MfETopoCodecNotFound)]
    [InlineData(ScreenConstants.MfETransformTypeNotSet)]
    [InlineData(ScreenConstants.VfwECodecApiFirst)]
    [InlineData(unchecked((int)0x80040312))]
    [InlineData(ScreenConstants.VfwECodecApiLast)]
    public void A_refused_parameter_or_configuration_retries_with_cbr(int hr)
    {
        Assert.True(RateControlFallbackPolicy.IsRejection(hr));
        Assert.True(RateControlFallbackPolicy.ShouldRetryWithCbr(RateControl.PeakConstrainedVbr, hr));
        Assert.False(RateControlFallbackPolicy.ShouldRetryWithCbr(RateControl.Cbr, hr)); // nothing left to fall back to
    }

    [Theory]
    [InlineData(ScreenConstants.EFail)]
    [InlineData(ScreenConstants.ESharingViolation)]
    [InlineData(ScreenConstants.ELockViolation)]
    [InlineData(unchecked((int)0x80070003))] // the folder does not exist
    [InlineData(unchecked((int)0x80070005))] // access denied
    [InlineData(unchecked((int)0x887A0005))] // DXGI_ERROR_DEVICE_REMOVED
    [InlineData(unchecked((int)0x8007000E))] // E_OUTOFMEMORY
    [InlineData(unchecked((int)0x8004030F))] // just below the ICodecAPI errors
    [InlineData(unchecked((int)0x80040315))] // just above them
    public void An_unrelated_failure_is_not_retried(int hr)
    {
        Assert.False(RateControlFallbackPolicy.IsRejection(hr));
        Assert.False(RateControlFallbackPolicy.ShouldRetryWithCbr(RateControl.PeakConstrainedVbr, hr));
    }

    [Fact]
    public void A_failed_cbr_retry_keeps_the_vbr_rejection_as_its_cause()
    {
        var rejected = new RateControlRejectedException("IMFSinkWriter::SetInputMediaType(RGB32, system memory) failed: 0x80070057", ScreenConstants.EInvalidArg);
        var failed = new EncoderSetupException("IMFSinkWriter::BeginWriting failed (after ...)", ScreenConstants.EFail, rejected);

        Assert.IsAssignableFrom<COMException>(failed); // callers catch COMException
        Assert.Equal(ScreenConstants.EFail, failed.HResult);
        Assert.Same(rejected, failed.InnerException);
        Assert.Equal(ScreenConstants.EInvalidArg, rejected.HResult);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void A_file_that_cannot_be_created_fails_at_once_without_a_cbr_retry()
    {
        var reason = ScreenTestEnvironment.SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        var path = Path.Combine(Path.GetTempPath(), "murmur-no-such-folder-" + Guid.NewGuid().ToString("N"), "screen.mp4");
        var stopwatch = Stopwatch.StartNew();

        var ex = Assert.ThrowsAny<COMException>(() => H264SinkWriter.Create(path, new H264EncoderSettings(new PixelSize(1920, 1080), 30, 6_000), EncoderInput.Cpu, 0));

        Assert.IsNotType<EncoderSetupException>(ex);
        Assert.IsNotType<RateControlRejectedException>(ex);
        Assert.Contains("MFCreateSinkWriterFromURL", ex.Message, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void VariantClear_EmptiesAScalarVariant()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "VariantClear (oleaut32) exists only on Windows.");
        var value = Variant.FromUInt32(12_000_000);
        Assert.Equal(ScreenConstants.SOk, ScreenNativeMethods.VariantClear(&value));
        Assert.Equal(0, value.Vt); // VT_EMPTY
        Assert.Null(value.AsUInt32());
    }
}
