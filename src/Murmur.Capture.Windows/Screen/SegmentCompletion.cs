using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Screen;

/// <summary>What happened to the black placeholder frame of a segment (see <see cref="PlaceholderFrame"/>).</summary>
internal enum PlaceholderStage
{
    /// <summary>Captured frames reached the encoder: no placeholder needed.</summary>
    NotNeeded,

    /// <summary>The sink writer had already failed; a placeholder would be rejected as well.</summary>
    SkippedWriterFailed,

    /// <summary>IMFVideoSampleAllocator::AllocateSample failed.</summary>
    AllocateFailed,

    /// <summary>Drawing the black picture into the sample's texture failed.</summary>
    FillFailed,

    /// <summary>IMFSinkWriter::WriteSample rejected the placeholder.</summary>
    WriteFailed,

    /// <summary>An exception escaped the placeholder path.</summary>
    Exception,

    /// <summary>IMFSinkWriter::WriteSample accepted the placeholder.</summary>
    Written,
}

/// <summary>Outcome of the placeholder attempt of one segment, with the HRESULT of the failing step (S_OK otherwise).</summary>
internal readonly record struct PlaceholderOutcome(PlaceholderStage Stage, int HResult)
{
    public static PlaceholderOutcome NotNeeded => new(PlaceholderStage.NotNeeded, ScreenConstants.SOk);

    public override string ToString() => Stage switch
    {
        PlaceholderStage.NotNeeded => "not needed",
        PlaceholderStage.SkippedWriterFailed => "skipped (sink writer already failed)",
        PlaceholderStage.AllocateFailed => $"AllocateSample failed (0x{HResult:X8})",
        PlaceholderStage.FillFailed => $"black fill failed (0x{HResult:X8})",
        PlaceholderStage.WriteFailed => $"WriteSample failed (0x{HResult:X8})",
        PlaceholderStage.Exception => $"exception (0x{HResult:X8})",
        _ => $"written (WriteSample 0x{HResult:X8})",
    };
}

/// <summary>How a stopped segment ends.</summary>
internal enum SegmentOutcome
{
    /// <summary>Finalize succeeded: the file is a valid MP4 and is kept.</summary>
    Completed,

    /// <summary>
    /// No captured frame reached the sink and Finalize failed (typically MF_E_SINK_NO_SAMPLES_PROCESSED): the partial
    /// file is not a valid MP4, so it is deleted and the segment ends without a file. Stopping does not throw.
    /// </summary>
    Empty,

    /// <summary>A segment with captured frames failed to finalize, or the encoder thread hung: stopping throws.</summary>
    Failed,
}

/// <summary>Everything the encoder thread reports about one stopped segment.</summary>
/// <param name="FinalizeResult">HRESULT of IMFSinkWriter::Finalize (E_FAIL when the encoder hung).</param>
/// <param name="EncoderHung">The encoder thread did not finish in time; its objects (and the file) are still in use.</param>
/// <param name="CapturedSamplesWritten">Captured frames IMFSinkWriter::WriteSample accepted in this segment.</param>
/// <param name="Placeholder">Outcome of the placeholder attempt.</param>
internal readonly record struct SegmentReport(int FinalizeResult, bool EncoderHung, long CapturedSamplesWritten, PlaceholderOutcome Placeholder);

/// <summary>Result of <see cref="SegmentCompletion.Complete"/>.</summary>
/// <param name="Empty">True when the segment ended without a file (see <see cref="SegmentOutcome.Empty"/>).</param>
/// <param name="Diagnostic">Placeholder outcome, Finalize HRESULT and what happened to the output file.</param>
internal readonly record struct SegmentResult(bool Empty, string Diagnostic);

/// <summary>
/// Decides how a stopped screen segment ends and applies it to the output file. Not a hot path (once per segment).
/// The rule: Finalize failing with MF_E_SINK_NO_SAMPLES_PROCESSED, or failing at all in a segment in which no captured
/// frame was written, means nothing worth keeping was recorded; the segment becomes empty (file deleted, no exception),
/// whatever the placeholder did. Failures of segments with captured frames, and a hung encoder, still throw.
/// </summary>
internal static class SegmentCompletion
{
    private const int DeleteAttempts = 5;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(100);

    public static SegmentOutcome Classify(in SegmentReport report)
    {
        if (report.EncoderHung)
        {
            return SegmentOutcome.Failed;
        }

        if (report.FinalizeResult >= 0)
        {
            return SegmentOutcome.Completed;
        }

        return report.FinalizeResult == ScreenConstants.MfESinkNoSamplesProcessed || report.CapturedSamplesWritten == 0
            ? SegmentOutcome.Empty
            : SegmentOutcome.Failed;
    }

    /// <summary>
    /// Keeps the file of a completed segment, deletes the partial file of an empty one (after the sink writer was
    /// released) and throws for a failed one.
    /// </summary>
    /// <exception cref="COMException">The segment failed; <see cref="Exception.HResult"/> is the Finalize HRESULT.</exception>
    public static SegmentResult Complete(string path, in SegmentReport report)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var prefix = $"captured samples: {report.CapturedSamplesWritten}; placeholder: {report.Placeholder}; finalize: 0x{report.FinalizeResult:X8}";
        switch (Classify(report))
        {
            case SegmentOutcome.Completed:
                return new SegmentResult(false, prefix + "; output: kept");
            case SegmentOutcome.Empty:
                return new SegmentResult(true, $"{prefix}; output: {DeleteOutput(path)}");
            default:
                var hint = report.EncoderHung
                    ? " (the encoder thread did not finish)"
                    : report.FinalizeResult == ScreenConstants.MfESinkNoSamplesProcessed ? " (no frame was captured)" : string.Empty;
                throw new COMException($"IMFSinkWriter::Finalize failed{hint}: HRESULT 0x{report.FinalizeResult:X8} ({prefix})", report.FinalizeResult);
        }
    }

    /// <summary>Deletes the partial output; retries briefly while Media Foundation still holds the file. Never throws.</summary>
    private static string DeleteOutput(string path)
    {
        if (!File.Exists(path))
        {
            return "no file";
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return "deleted";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DeleteAttempts)
                {
                    return $"delete failed ({ex.GetType().Name}: {ex.Message})";
                }

                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }
}
