namespace Murmur.Capture.Windows.Screen;

/// <summary>How a segment's frames reach the H.264 encoder.</summary>
internal enum EncoderInput
{
    /// <summary>
    /// Zero-copy: D3D11 textures from an <c>IMFVideoSampleAllocatorEx</c> filled on the GPU, a sink writer with the DXGI
    /// device manager and hardware MFTs. Only used when the device is a real GPU with video support and a hardware
    /// H.264 encoder MFT exists.
    /// </summary>
    Gpu,

    /// <summary>
    /// CPU readback for the software encoder: the frame is copied into a staging texture, mapped and copied or scaled
    /// on the CPU into a system-memory RGB32 sample; the sink writer gets no device manager. Needs neither D3D11 video
    /// support nor Media Foundation's D3D11 pipeline (unavailable or failing on WARP / the Basic Render Driver).
    /// </summary>
    Cpu,
}

/// <summary>Chooses the <see cref="EncoderInput"/> of a segment (pure).</summary>
internal static class FramePathPolicy
{
    /// <summary>
    /// Zero-copy only for a hardware encoder on a real GPU: a software encoder reads system memory anyway, and feeding
    /// it D3D11 samples (Media Foundation converts ARGB32 to NV12 on the device first) is the combination that failed
    /// on windows-latest (WARP, no D3D11 video support).
    /// </summary>
    public static EncoderInput Choose(bool videoSupport, bool softwareRenderer, int hardwareH264Encoders) =>
        videoSupport && !softwareRenderer && hardwareH264Encoders > 0 ? EncoderInput.Gpu : EncoderInput.Cpu;

    /// <summary>
    /// The highest frame rate of <see cref="EncoderInput.Cpu"/>: every frame is read back, converted and encoded in
    /// software, so 1080p60 would not fit the CPU budget of a recording (&lt; 8 %).
    /// </summary>
    public const int CpuMaxFrameRate = 30;

    /// <summary>
    /// The encoder settings of a segment on <paramref name="input"/>: the zero-copy path encodes what was asked for;
    /// CPU readback caps the frame rate at <see cref="CpuMaxFrameRate"/> and scales the bit rate with it, so a frame
    /// gets as many bits as at the requested rate.
    /// <para>
    /// Keyframes are forced (<see cref="H264EncoderSettings.KeyFrameInterval"/>) on the zero-copy path only. The hardware
    /// encoder encodes exactly the samples it is given, so on a static screen (about one repeat a second) its GOP of
    /// frames would put minutes between keyframes. The CPU readback pipeline (the Microsoft H.264 encoder behind the
    /// sink writer's RGB32 converter) emits a constant frame rate whatever the sample times (150 samples for 5 s of a
    /// static screen at 30 fps), so its GOP of 2 s of frames is already 2 s of media time; requests there land on
    /// whatever frame the encoder is at and only add keyframes (KeyFrameIntervalTests measure both).
    /// </para>
    /// </summary>
    public static H264EncoderSettings Settings(EncoderInput input, H264EncoderSettings requested)
    {
        if (input == EncoderInput.Gpu)
        {
            return requested;
        }

        var cpu = requested with { KeyFrameInterval = 0 };
        if (requested.FrameRate <= CpuMaxFrameRate)
        {
            return cpu;
        }

        var bitrate = (int)Math.Max(1, (long)requested.BitrateKbps * CpuMaxFrameRate / requested.FrameRate);
        return cpu with { FrameRate = CpuMaxFrameRate, BitrateKbps = bitrate };
    }

    /// <summary>Why <see cref="Choose"/> picked what it picked (diagnostics).</summary>
    public static string Describe(bool videoSupport, bool softwareRenderer, int hardwareH264Encoders) =>
        Choose(videoSupport, softwareRenderer, hardwareH264Encoders) switch
        {
            EncoderInput.Gpu => $"GPU zero-copy ({hardwareH264Encoders} hardware H.264 encoder MFT{(hardwareH264Encoders == 1 ? string.Empty : "s")})",
            _ when softwareRenderer => "CPU readback: software renderer (WARP / Microsoft Basic Render Driver), no GPU encoder",
            _ when !videoSupport => "CPU readback: the D3D11 device has no video support",
            _ => "CPU readback: no hardware H.264 encoder MFT",
        };
}

/// <summary>The step of the frame path that failed (diagnostics for fault messages).</summary>
internal enum FrameStep
{
    None,
    CaptureSurface,
    SurfaceAccess,
    SurfaceTexture,
    SampleCreate,
    SampleBuffer,
    SampleCache,
    SampleFormat,
    DxgiBuffer,
    BufferTexture,
    BufferSubresource,
    BufferLength,
    BufferTooSmall,
    LockBuffer,
    VideoDevice,
    VideoContext,
    ProcessorTexture,
    ProcessorEnumerator,
    Processor,
    ProcessorInputView,
    ProcessorOutputView,
    ProcessorBlt,
    BlackTexture,
    SourceFormat,
    StagingTexture,
    MapStaging,
}

/// <summary>A failed frame-path step and its HRESULT.</summary>
internal readonly record struct FrameFailure(FrameStep Step, int HResult)
{
    public override string ToString() => $"{FrameSteps.Describe(Step)}: HRESULT 0x{HResult:X8}";
}

/// <summary>Names the API behind each <see cref="FrameStep"/> (pure).</summary>
internal static class FrameSteps
{
    public static string Describe(FrameStep step) => step switch
    {
        FrameStep.CaptureSurface => "IDirect3D11CaptureFrame::get_Surface",
        FrameStep.SurfaceAccess => "QueryInterface(IDirect3DDxgiInterfaceAccess) on the capture surface",
        FrameStep.SurfaceTexture => "IDirect3DDxgiInterfaceAccess::GetInterface(ID3D11Texture2D)",
        FrameStep.SampleCreate => "MFCreateSample / MFCreateMemoryBuffer",
        FrameStep.SampleBuffer => "IMFSample::GetBufferByIndex",
        FrameStep.SampleCache => "the sample cache (unknown or too many samples)",
        FrameStep.SampleFormat => "the sample texture check (not single-sampled B8G8R8A8)",
        FrameStep.DxgiBuffer => "QueryInterface(IMFDXGIBuffer) on the sample buffer",
        FrameStep.BufferTexture => "IMFDXGIBuffer::GetResource(ID3D11Texture2D)",
        FrameStep.BufferSubresource => "IMFDXGIBuffer::GetSubresourceIndex",
        FrameStep.BufferLength => "IMFMediaBuffer::SetCurrentLength",
        FrameStep.BufferTooSmall => "the sample buffer size check",
        FrameStep.LockBuffer => "IMFMediaBuffer::Lock",
        FrameStep.VideoDevice => "QueryInterface(ID3D11VideoDevice) on the D3D11 device (no video support)",
        FrameStep.VideoContext => "QueryInterface(ID3D11VideoContext) on the immediate context",
        FrameStep.ProcessorTexture => "ID3D11Device::CreateTexture2D(video processor input)",
        FrameStep.ProcessorEnumerator => "ID3D11VideoDevice::CreateVideoProcessorEnumerator",
        FrameStep.Processor => "ID3D11VideoDevice::CreateVideoProcessor",
        FrameStep.ProcessorInputView => "ID3D11VideoDevice::CreateVideoProcessorInputView",
        FrameStep.ProcessorOutputView => "ID3D11VideoDevice::CreateVideoProcessorOutputView",
        FrameStep.ProcessorBlt => "ID3D11VideoContext::VideoProcessorBlt",
        FrameStep.BlackTexture => "ID3D11Device::CreateTexture2D(black placeholder)",
        FrameStep.SourceFormat => "the capture texture check (not single-sampled 32-bit BGRA)",
        FrameStep.StagingTexture => "ID3D11Device::CreateTexture2D(staging)",
        FrameStep.MapStaging => "ID3D11DeviceContext::Map(staging, D3D11_MAP_READ)",
        _ => "an unknown step",
    };
}

/// <summary>Fault texts of the frame path (pure).</summary>
internal static class FrameCopyNaming
{
    public static string Path(EncoderInput input) => input == EncoderInput.Gpu ? "GPU zero-copy" : "CPU readback";

    /// <summary>
    /// E.g. "GPU frame copy failed at QueryInterface(ID3D11VideoDevice) ...: HRESULT 0x80004002"; without a matching
    /// recorded step just the HRESULT.
    /// </summary>
    public static string Fault(EncoderInput input, FrameFailure failure, int hr)
    {
        var prefix = input == EncoderInput.Gpu ? "GPU frame copy failed" : "CPU readback frame copy failed";
        return failure.Step != FrameStep.None && failure.HResult == hr
            ? $"{prefix} at {failure}"
            : $"{prefix}: HRESULT 0x{hr:X8}";
    }
}

/// <summary>
/// Produces and fills the encoder samples of one segment: <see cref="GpuFrameCopier"/> (zero-copy D3D11 samples) or
/// <see cref="CpuFrameCopier"/> (system-memory samples). Every fill also sets the buffer's current length (Media
/// Foundation rejects samples whose buffers report 0 bytes). Methods return HRESULTs and record the failing step in
/// <see cref="LastFailure"/>; allocation-free in steady state. Not thread-safe: the recorder serializes all calls
/// with its copy lock. Dispose after the sink writer was released.
/// </summary>
internal interface IFramePath : IDisposable
{
    EncoderInput Input { get; }

    /// <summary>The step and HRESULT of the last failed call (diagnostics).</summary>
    FrameFailure LastFailure { get; }

    /// <summary>
    /// Takes a free sample (the caller owns one reference). MF_E_SAMPLEALLOCATOR_EMPTY when every sample is still in
    /// use (back-pressure: drop the frame).
    /// </summary>
    int AllocateSample(out nint sample);

    /// <summary>Copies (or scales) the capture texture <paramref name="source"/> with valid region <paramref name="content"/> into <paramref name="sample"/>.</summary>
    int CopyFrame(nint sample, nint source, PixelSize content);

    /// <summary>Copies the whole picture of <paramref name="source"/> into <paramref name="destination"/> (frame repeat).</summary>
    int CopySample(nint destination, nint source);

    /// <summary>Fills <paramref name="sample"/> with opaque black (the placeholder of a segment without frames).</summary>
    int FillBlack(nint sample);
}
