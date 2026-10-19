namespace Murmur.Core.Capture;

/// <summary>Format of the PCM stream an <see cref="IAudioSource"/> delivers. Samples are always 32-bit float, interleaved.</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels)
{
    /// <summary>Format used for everything stored on disk and fed to ASR: 16 kHz mono.</summary>
    public static AudioFormat Asr { get; } = new(16_000, 1);
}

public enum AudioDeviceKind
{
    /// <summary>Capture endpoint (microphone). Recorded as the "Me" track.</summary>
    Microphone,

    /// <summary>Render endpoint captured in loopback mode. Recorded as the "Others" track.</summary>
    SystemOutput,
}

public sealed record AudioDevice(string Id, string Name, AudioDeviceKind Kind, bool IsDefault);

/// <summary>What kind of problem a <see cref="CaptureFault"/> is, so callers never parse <see cref="CaptureFault.Message"/>.</summary>
public enum CaptureFaultKind
{
    Other,

    /// <summary>Frames were lost (e.g. the screen encoder's sample pool ran dry).</summary>
    FramesDropped,

    /// <summary>The screen recorder fell back from the GPU path to CPU readback: it records on, at a higher CPU cost.</summary>
    CpuFallback,
}

/// <param name="Message">English, for logs.</param>
public sealed record CaptureFault(string Source, string Message, bool IsFatal, CaptureFaultKind Kind = CaptureFaultKind.Other);
