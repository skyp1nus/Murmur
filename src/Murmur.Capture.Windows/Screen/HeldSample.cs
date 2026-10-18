using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Screen;

/// <summary>Where the encoder thread writes its samples (<see cref="H264SinkWriter"/>; tests use fakes).</summary>
internal interface ISampleSink
{
    /// <summary>Writes <paramref name="sample"/>; the sink takes its own reference. Returns the HRESULT.</summary>
    int WriteSample(nint sample);
}

/// <summary>How taking a copy of the held frame for a static-screen repeat ended (see <see cref="HeldSample.CopyForRepeat"/>).</summary>
internal enum RepeatCopy
{
    /// <summary>The copy is ready to be submitted.</summary>
    Copied,

    /// <summary>
    /// Every sample is in use (MF_E_SAMPLEALLOCATOR_EMPTY): the repeat is skipped and counted; the held frame simply
    /// lasts longer.
    /// </summary>
    PoolEmpty,

    /// <summary>Allocating the sample failed for another reason: a fatal fault, like for a captured frame.</summary>
    AllocateFailed,

    /// <summary>The copy failed (e.g. the device was removed): a fatal fault, like a failed captured frame.</summary>
    CopyFailed,
}

/// <summary>
/// Ownership hand-offs of the encoder thread's held sample (see <see cref="HeldFrameTiming"/>), kept out of the
/// recorder so the exception and failure paths can be tested with fake COM objects. Every path releases exactly the
/// references it was given, including managed exceptions. Allocation-free on success.
/// </summary>
internal static class HeldSample
{
    public const string SetSampleTimeStep = "IMFSample::SetSampleTime";
    public const string SetSampleDurationStep = "IMFSample::SetSampleDuration";
    public const string WriteSampleStep = "IMFSinkWriter::WriteSample";

    /// <summary>
    /// Timestamps and writes the held sample and releases it: <paramref name="held"/> is cleared before anything can
    /// fail, and its reference is released on every path (the sink keeps its own reference while it encodes the
    /// sample). Never throws: a managed exception (e.g. no sink) is returned as its HRESULT (E_FAIL if it has none) in
    /// <paramref name="exception"/>. <paramref name="step"/> names the call that failed, or the last one made.
    /// </summary>
    public static int Write(ref nint held, ISampleSink? sink, long time, long duration, out string step, out Exception? exception)
    {
        var sample = held;
        held = 0;
        step = SetSampleTimeStep;
        exception = null;
        try
        {
            var hr = IMFSample.SetSampleTime(sample, time);
            if (hr >= 0)
            {
                step = SetSampleDurationStep;
                hr = IMFSample.SetSampleDuration(sample, duration);
            }

            if (hr >= 0)
            {
                step = WriteSampleStep;
                hr = sink!.WriteSample(sample);
            }

            return hr;
        }
        catch (Exception ex)
        {
            exception = ex;
            return ex.HResult < 0 ? ex.HResult : ScreenConstants.EFail;
        }
        finally
        {
            ComCall.Release(ref sample);
        }
    }

    /// <summary>
    /// Takes a free sample from <paramref name="frames"/> and copies the held sample <paramref name="held"/> into it
    /// (under <paramref name="copyLock"/>). Only <see cref="RepeatCopy.Copied"/> hands a sample to the caller in
    /// <paramref name="copy"/>; on every other outcome, and when the copy throws, the new sample is released again.
    /// <paramref name="hr"/> and <paramref name="failure"/> describe a failed allocation or copy.
    /// </summary>
    public static RepeatCopy CopyForRepeat(IFramePath frames, Lock copyLock, nint held, out nint copy, out int hr, out FrameFailure failure)
    {
        ArgumentNullException.ThrowIfNull(frames);
        copy = 0;
        failure = default;
        nint sample = 0;
        try
        {
            lock (copyLock)
            {
                hr = frames.AllocateSample(out sample);
                if (hr < 0 || sample == 0)
                {
                    ComCall.Release(ref sample);
                    if (hr == ScreenConstants.MfESampleAllocatorEmpty)
                    {
                        return RepeatCopy.PoolEmpty;
                    }

                    if (hr >= 0)
                    {
                        hr = ScreenConstants.EPointer; // "success" without a sample
                    }

                    return RepeatCopy.AllocateFailed;
                }

                hr = frames.CopySample(sample, held);
                if (hr < 0)
                {
                    failure = frames.LastFailure;
                    ComCall.Release(ref sample);
                    return RepeatCopy.CopyFailed;
                }
            }

            copy = sample;
            sample = 0;
            return RepeatCopy.Copied;
        }
        finally
        {
            ComCall.Release(ref sample); // only set when AllocateSample or CopySample threw
        }
    }

    /// <summary>The fatal fault text of a failed repeat (<see cref="RepeatCopy.AllocateFailed"/> or <see cref="RepeatCopy.CopyFailed"/>).</summary>
    public static string RepeatFault(RepeatCopy outcome, EncoderInput input, FrameFailure failure, int hr) => outcome switch
    {
        RepeatCopy.AllocateFailed => $"Allocating an encoder sample to repeat the held frame failed ({FrameCopyNaming.Path(input)}): {ComHelpers.Describe(hr)}",
        RepeatCopy.CopyFailed => "Repeating the held frame failed: " + FrameCopyNaming.Fault(input, failure, hr),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not a failed repeat."),
    };
}
