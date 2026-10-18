using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>How a stopped screen segment ends: empty segments are dropped quietly, real failures throw (pure logic, any OS).</summary>
public sealed class SegmentCompletionTests : IDisposable
{
    private const int NoSamples = ScreenConstants.MfESinkNoSamplesProcessed;
    private const int DiskFull = unchecked((int)0x80070070); // HRESULT_FROM_WIN32(ERROR_DISK_FULL)

    private static readonly PlaceholderOutcome Written = new(PlaceholderStage.Written, ScreenConstants.SOk);
    private static readonly PlaceholderOutcome Rejected = new(PlaceholderStage.WriteFailed, unchecked((int)0x80070057));

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"murmur-segment-{Guid.NewGuid():N}.mp4");

    public SegmentCompletionTests() => File.WriteAllBytes(_path, [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p']);

    public void Dispose() => File.Delete(_path);

    [Theory]
    [InlineData(0, false, 0, (int)SegmentOutcome.Completed)]          // placeholder-only file finalized fine
    [InlineData(0, false, 90, (int)SegmentOutcome.Completed)]
    [InlineData(NoSamples, false, 0, (int)SegmentOutcome.Empty)]      // the CI case: nothing reached the sink
    [InlineData(NoSamples, false, 5, (int)SegmentOutcome.Empty)]      // the sink processed nothing: no valid file either way
    [InlineData(DiskFull, false, 0, (int)SegmentOutcome.Empty)]       // any failure without captured frames
    [InlineData(DiskFull, false, 1, (int)SegmentOutcome.Failed)]      // a real failure of a real recording
    [InlineData(ScreenConstants.EFail, true, 0, (int)SegmentOutcome.Failed)] // hung encoder: the file is still in use
    [InlineData(ScreenConstants.EFail, true, 7, (int)SegmentOutcome.Failed)]
    public void Classify(int finalizeResult, bool hung, long captured, int expected) =>
        Assert.Equal((SegmentOutcome)expected, SegmentCompletion.Classify(new SegmentReport(finalizeResult, hung, captured, Written)));

    [Fact]
    public void NoSamplesProcessed_DeletesThePartialFile_AndDoesNotThrow()
    {
        var result = SegmentCompletion.Complete(_path, new SegmentReport(NoSamples, false, 0, Rejected));

        Assert.True(result.Empty);
        Assert.False(File.Exists(_path));
        Assert.Contains("placeholder: WriteSample failed (0x80070057)", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("finalize: 0xC00D4A44", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("output: deleted", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSamplesProcessed_EvenAfterAWrittenPlaceholder_IsEmpty()
    {
        var result = SegmentCompletion.Complete(_path, new SegmentReport(NoSamples, false, 0, Written));

        Assert.True(result.Empty);
        Assert.False(File.Exists(_path));
        Assert.Contains("placeholder: written (WriteSample 0x00000000)", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void AnyFailureWithoutCapturedFrames_IsEmpty()
    {
        var result = SegmentCompletion.Complete(_path, new SegmentReport(DiskFull, false, 0, new PlaceholderOutcome(PlaceholderStage.AllocateFailed, ScreenConstants.EFail)));

        Assert.True(result.Empty);
        Assert.False(File.Exists(_path));
        Assert.Contains("AllocateSample failed (0x80004005)", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("finalize: 0x80070070", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySegment_WithoutAFile_ReportsIt()
    {
        File.Delete(_path);

        var result = SegmentCompletion.Complete(_path, new SegmentReport(NoSamples, false, 0, Written));

        Assert.True(result.Empty);
        Assert.Contains("output: no file", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureOfASegmentWithCapturedFrames_Throws_AndKeepsTheFile()
    {
        var ex = Assert.Throws<COMException>(() => SegmentCompletion.Complete(_path, new SegmentReport(DiskFull, false, 42, PlaceholderOutcome.NotNeeded)));

        Assert.Equal(DiskFull, ex.HResult);
        Assert.Contains("HRESULT 0x80070070", ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(_path)); // a fragmented MP4 stays playable up to its last fragment
    }

    [Fact]
    public void HungEncoder_Throws_EvenWithoutCapturedFrames()
    {
        var ex = Assert.Throws<COMException>(() => SegmentCompletion.Complete(_path, new SegmentReport(ScreenConstants.EFail, true, 0, PlaceholderOutcome.NotNeeded)));

        Assert.Equal(ScreenConstants.EFail, ex.HResult);
        Assert.Contains("did not finish", ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void Success_KeepsTheFile()
    {
        var result = SegmentCompletion.Complete(_path, new SegmentReport(ScreenConstants.SOk, false, 0, Written));

        Assert.False(result.Empty);
        Assert.True(File.Exists(_path));
        Assert.Contains("output: kept", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_RejectsAnEmptyPath() =>
        Assert.Throws<ArgumentException>(() => SegmentCompletion.Complete(string.Empty, new SegmentReport(0, false, 0, Written)));

    [Theory]
    [InlineData((int)PlaceholderStage.NotNeeded, 0, "not needed")]
    [InlineData((int)PlaceholderStage.SkippedWriterFailed, 0, "skipped (sink writer already failed)")]
    [InlineData((int)PlaceholderStage.AllocateFailed, unchecked((int)0xC00D6D60), "AllocateSample failed (0xC00D6D60)")]
    [InlineData((int)PlaceholderStage.FillFailed, unchecked((int)0x887A0005), "black fill failed (0x887A0005)")]
    [InlineData((int)PlaceholderStage.WriteFailed, unchecked((int)0x80070057), "WriteSample failed (0x80070057)")]
    [InlineData((int)PlaceholderStage.Exception, unchecked((int)0x80131500), "exception (0x80131500)")]
    [InlineData((int)PlaceholderStage.Written, 0, "written (WriteSample 0x00000000)")]
    public void PlaceholderOutcome_Describes(int stage, int hr, string expected) =>
        Assert.Equal(expected, new PlaceholderOutcome((PlaceholderStage)stage, hr).ToString());
}
