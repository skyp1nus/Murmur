using Murmur.Capture.Windows.Video.Interop;

namespace Murmur.Capture.Windows.Video;

/// <summary>
/// Decides when <c>TryCopyFrame</c> has a new picture to copy (pure; one per player, UI thread). A picture is due when
/// the Media Engine presented a frame with another time stamp than the last copied one (OnVideoStreamTick), when a
/// "the picture changed" event arrived since the last copy (first frame, seek completed), or when the caller asks for
/// another size. Allocation-free.
/// </summary>
internal struct FrameChangeTracker
{
    private const long None = long.MinValue;

    private long _presentedPts;
    private long _copiedPts;
    private int _copiedWidth;
    private int _copiedHeight;
    private int _copiedEvents;
    private bool _hasCopy;

    public FrameChangeTracker()
    {
        Reset();
    }

    /// <summary>Time stamp (100 ns) of the frame copied last; null before the first copy.</summary>
    public readonly long? CopiedPts => _hasCopy && _copiedPts != None ? _copiedPts : null;

    /// <summary>Forgets everything (a new file was opened).</summary>
    public void Reset()
    {
        _presentedPts = None;
        _copiedPts = None;
        _copiedWidth = 0;
        _copiedHeight = 0;
        _copiedEvents = 0;
        _hasCopy = false;
    }

    /// <summary>Records the result of IMFMediaEngine::OnVideoStreamTick: S_OK with a time stamp = a frame is presented.</summary>
    public void Tick(int hr, long pts)
    {
        if (hr == 0 && pts >= 0)
        {
            _presentedPts = pts;
        }
    }

    /// <summary>
    /// True when a copy at <paramref name="width"/> x <paramref name="height"/> would show something new.
    /// <paramref name="events"/> is the player's count of picture-changing engine events.
    /// </summary>
    public readonly bool IsDue(int width, int height, int events)
    {
        if (!_hasCopy)
        {
            return _presentedPts != None || events != 0;
        }

        return width != _copiedWidth || height != _copiedHeight || events != _copiedEvents || _presentedPts != _copiedPts;
    }

    /// <summary>A copy succeeded with the state read before it.</summary>
    public void Copied(int width, int height, int events)
    {
        _hasCopy = true;
        _copiedWidth = width;
        _copiedHeight = height;
        _copiedEvents = events;
        _copiedPts = _presentedPts;
    }
}

/// <summary>Argument checks and row copies of <c>IVideoPlayer.TryCopyFrame</c> (pure).</summary>
internal static unsafe class BgraFrame
{
    public const int BytesPerPixel = 4;

    /// <summary>Throws when the buffer cannot hold <paramref name="height"/> rows of <paramref name="width"/> BGRA pixels.</summary>
    public static void Validate(int length, int width, int height, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 16_384);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, 16_384);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * BytesPerPixel);
        if ((long)stride * (height - 1) + ((long)width * BytesPerPixel) > length)
        {
            throw new ArgumentException($"The destination holds {length} bytes; {width}x{height} BGRA with stride {stride} needs {(long)stride * (height - 1) + ((long)width * BytesPerPixel)}.", "destination");
        }
    }

    /// <summary>
    /// Copies <paramref name="height"/> rows of <paramref name="width"/> BGRA pixels from <paramref name="source"/>
    /// (<paramref name="sourcePitch"/> bytes per row) into <paramref name="destination"/> (<paramref name="stride"/>).
    /// </summary>
    public static void CopyRows(byte* source, int sourcePitch, Span<byte> destination, int width, int height, int stride)
    {
        var rowBytes = width * BytesPerPixel;
        if (sourcePitch == stride && stride == rowBytes)
        {
            new ReadOnlySpan<byte>(source, rowBytes * height).CopyTo(destination);
            return;
        }

        for (var y = 0; y < height; y++)
        {
            new ReadOnlySpan<byte>(source + ((long)y * sourcePitch), rowBytes).CopyTo(destination.Slice(y * stride, rowBytes));
        }
    }
}

/// <summary>Turns a Media Engine error (MF_MEDIA_ENGINE_ERR + HRESULT) into English text and exceptions (pure).</summary>
internal static class MediaEngineErrors
{
    public static string Name(ushort code) => code switch
    {
        VideoConstants.ErrNoError => "no error",
        VideoConstants.ErrAborted => "MF_MEDIA_ENGINE_ERR_ABORTED",
        VideoConstants.ErrNetwork => "MF_MEDIA_ENGINE_ERR_NETWORK",
        VideoConstants.ErrDecode => "MF_MEDIA_ENGINE_ERR_DECODE",
        VideoConstants.ErrSrcNotSupported => "MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED",
        VideoConstants.ErrEncrypted => "MF_MEDIA_ENGINE_ERR_ENCRYPTED",
        _ => $"media engine error {code}",
    };

    /// <summary>E.g. "MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED (HRESULT 0xC00D36C4)".</summary>
    public static string Describe(ushort code, int hr) => hr == 0 ? Name(code) : $"{Name(code)} (HRESULT 0x{hr:X8})";

    /// <summary>
    /// The exception for an open that failed: <see cref="InvalidDataException"/> when the file cannot be decoded or is
    /// not a supported media file, <see cref="IOException"/> otherwise; the inner <see cref="System.Runtime.InteropServices.COMException"/>
    /// carries the HRESULT.
    /// </summary>
    public static Exception OpenFailed(string path, ushort code, int hr)
    {
        var message = $"The video '{Path.GetFileName(path)}' could not be opened: {Describe(code, hr)}.";
        var inner = new System.Runtime.InteropServices.COMException(message, hr == 0 ? VideoConstants.MfEInvalidRequest : hr);
        return code is VideoConstants.ErrDecode or VideoConstants.ErrSrcNotSupported || hr == VideoConstants.MfEUnsupportedByteStreamType
            ? new InvalidDataException(message, inner)
            : new IOException(message, inner);
    }
}

/// <summary>Where the Media Engine puts the pictures.</summary>
internal enum VideoOutput
{
    /// <summary>Direct3D 11 on a real GPU with video support, else WIC.</summary>
    Auto,

    /// <summary>
    /// D3D11 device + IMFDXGIDeviceManager (hardware decoding), frames transferred into a BGRA render target and read back
    /// through a staging texture.
    /// </summary>
    Direct3D,

    /// <summary>No D3D device: software decoding, frames transferred into a WIC bitmap (machines without a usable GPU).</summary>
    Wic,
}

/// <summary>Chooses the output of a player (pure).</summary>
internal static class VideoOutputPolicy
{
    /// <summary>
    /// <see cref="VideoOutput.Direct3D"/> when asked for, or for <see cref="VideoOutput.Auto"/> when a hardware device
    /// with video support exists; otherwise <see cref="VideoOutput.Wic"/> (WARP and the Basic Render Driver have no video
    /// decoder or processor, D-027).
    /// </summary>
    public static VideoOutput Choose(VideoOutput requested, bool deviceCreated, bool softwareRenderer, bool videoSupport) => requested switch
    {
        VideoOutput.Direct3D => VideoOutput.Direct3D,
        VideoOutput.Wic => VideoOutput.Wic,
        _ => deviceCreated && !softwareRenderer && videoSupport ? VideoOutput.Direct3D : VideoOutput.Wic,
    };
}
