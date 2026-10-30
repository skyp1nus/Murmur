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
    /// <summary>The "no time stamp" value of <see cref="PresentedPts"/>.</summary>
    internal const long None = long.MinValue;

    private const long MaxFramePeriod = TimeSpan.TicksPerSecond;

    private long _presentedPts;
    private long _framePeriod;
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

    /// <summary>Time stamp (100 ns) of the frame the engine presented last, <see cref="None"/> before the first one.</summary>
    public readonly long PresentedPts => _presentedPts;

    /// <summary>
    /// The shortest distance (100 ns) seen between two frames presented one after the other while playing; 0 until two
    /// such frames were seen. A frame lasts at most this long (a constant-rate stream: exactly this long).
    /// </summary>
    public readonly long FramePeriod => _framePeriod;

    /// <summary>At least one copy succeeded since the last <see cref="Reset"/>.</summary>
    public readonly bool HasCopy => _hasCopy;

    /// <summary>Forgets everything (a new file was opened).</summary>
    public void Reset()
    {
        _presentedPts = None;
        _framePeriod = 0;
        _copiedPts = None;
        _copiedWidth = 0;
        _copiedHeight = 0;
        _copiedEvents = 0;
        _hasCopy = false;
    }

    /// <summary>Records the result of IMFMediaEngine::OnVideoStreamTick: S_OK with a time stamp = a frame is presented.</summary>
    public void Tick(int hr, long pts) => Tick(hr, pts, observePeriod: false);

    /// <summary>
    /// Like <see cref="Tick(int, long)"/>; with <paramref name="observePeriod"/> (the picture moves on its own: no seek
    /// is involved) the distance to the previously presented frame feeds <see cref="FramePeriod"/>.
    /// </summary>
    public void Tick(int hr, long pts, bool observePeriod)
    {
        if (hr == 0 && pts >= 0)
        {
            var distance = pts - _presentedPts;
            if (observePeriod && _presentedPts != None && distance > 0 && distance <= MaxFramePeriod
                && (_framePeriod == 0 || distance < _framePeriod))
            {
                _framePeriod = distance;
            }

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

/// <summary>
/// Serializes seeks for the Media Engine (pure; UI thread). A seek requested while another one is in flight is not sent
/// at once (the engine may coalesce or drop it): the latest such target is kept and sent when the engine reports the
/// first one done (MF_MEDIA_ENGINE_EVENT_SEEKED, counted by the caller) or after <see cref="Timeout"/>. While anything is
/// in flight or queued, <see cref="LatestTarget"/> is the position the player reports.
/// </summary>
internal struct SeekQueue
{
    private bool _inFlight;
    private long _issuedAt;
    private int _seekedAtIssue;
    private TimeSpan _inFlightTarget;
    private bool _hasQueued;
    private TimeSpan _queued;

    public SeekQueue(long timeout)
    {
        Timeout = timeout;
    }

    /// <summary>How long (in the caller's timestamp units) an in-flight seek is waited for at most.</summary>
    public long Timeout { get; }

    /// <summary>A seek is in flight or queued.</summary>
    public readonly bool IsBusy => _inFlight || _hasQueued;

    /// <summary>The newest requested target (meaningful while <see cref="IsBusy"/>).</summary>
    public readonly TimeSpan LatestTarget => _hasQueued ? _queued : _inFlightTarget;

    public void Reset()
    {
        _inFlight = false;
        _hasQueued = false;
    }

    /// <summary>
    /// A seek to <paramref name="target"/> is requested; true when the caller must send it now (nothing in flight),
    /// false when it was queued behind the one in flight.
    /// </summary>
    public bool Request(TimeSpan target, int seekedEvents, long now)
    {
        Complete(seekedEvents, now);
        if (_inFlight)
        {
            _queued = target;
            _hasQueued = true;
            return false;
        }

        Issue(target, seekedEvents, now);
        return true;
    }

    /// <summary>True (with the target) when the in-flight seek is done and a queued one must be sent now.</summary>
    public bool TryNext(int seekedEvents, long now, out TimeSpan target)
    {
        Complete(seekedEvents, now);
        if (!_inFlight && _hasQueued)
        {
            target = _queued;
            _hasQueued = false;
            Issue(target, seekedEvents, now);
            return true;
        }

        target = default;
        return false;
    }

    private void Issue(TimeSpan target, int seekedEvents, long now)
    {
        _inFlight = true;
        _inFlightTarget = target;
        _seekedAtIssue = seekedEvents;
        _issuedAt = now;
    }

    private void Complete(int seekedEvents, long now)
    {
        if (_inFlight && (seekedEvents != _seekedAtIssue || now - _issuedAt >= Timeout))
        {
            _inFlight = false;
        }
    }
}

/// <summary>What <see cref="SeekRecovery"/> asks the player to do (the cures are tried in this order).</summary>
internal enum SeekRecoveryStep
{
    /// <summary>Nothing (yet).</summary>
    None = 0,

    /// <summary>Copy the presented frame once more although the engine reports no new one.</summary>
    Retransfer = 1,

    /// <summary>Seek clearly away from the target (the frame there is only a detour).</summary>
    SeekAway = 2,

    /// <summary>Seek to the target again after <see cref="SeekAway"/>.</summary>
    SeekBack = 3,

    /// <summary>IMFMediaEngineEx::SetCurrentTimeEx(target, NORMAL).</summary>
    SeekExNormal = 4,

    /// <summary>IMFMediaEngineEx::SetCurrentTimeEx(target, APPROXIMATE).</summary>
    SeekExApproximate = 5,

    /// <summary>IMFMediaEngineEx::FrameStep(forward).</summary>
    FrameStep = 6,

    /// <summary>Seek to the target again after <see cref="FrameStep"/>.</summary>
    FrameStepBack = 7,

    /// <summary>Play() immediately followed by Pause().</summary>
    PlayPause = 8,
}

/// <summary>
/// Self-recovery for a rare Media Engine race (D-035): after a seek while paused, SEEKED arrives (~5 ms), currentTime is the
/// target, but the presented frame is still the old one and OnVideoStreamTick never reports another time stamp, so the old
/// picture stays up. Armed by every seek request. The seek worked as soon as the presented frame is the target's (it starts
/// at or before the target, no more than one frame period before it; whenever it arrived) or, after the queue went idle,
/// another time stamp than before was presented. Frames presented while a seek is in flight or queued belong to older
/// targets and only move the baseline. When the queue has been idle for <see cref="Delay"/> without success and the engine is
/// settled (paused, not seeking, has data), it asks for the next of <see cref="MaxAttempts"/> cures, one per wait, in the
/// order of <see cref="SeekRecoveryStep"/> (this ladder is diagnostic: which one un-sticks the engine is read from
/// <see cref="TakeHealedBy"/> in the CI log). Pure; one per player, UI thread; allocation-free.
/// </summary>
internal struct SeekRecovery
{
    /// <summary>Cures tried per seek target at most (the steps from <see cref="SeekRecoveryStep.Retransfer"/> to <see cref="SeekRecoveryStep.PlayPause"/>).</summary>
    public const int MaxAttempts = 8;

    /// <summary>Checks that found the engine not settled (playing, seeking, no data) before the recovery gives up.</summary>
    public const int MaxUnsettled = 10;

    /// <summary>Length (100 ns) of the frame assumed while the frame period is unknown.</summary>
    public const long DefaultFramePeriod = 400_000;

    private static readonly TimeSpan Away = TimeSpan.FromMilliseconds(200);

    private bool _armed;
    private bool _ignoreNewFrame;
    private long _baselinePts;
    private long _quietSince;
    private int _attempts;
    private int _unsettled;
    private SeekRecoveryStep _lastStep;
    private SeekRecoveryStep _healedBy;
    private TimeSpan _target;

    public SeekRecovery(long delay)
    {
        Delay = delay;
        _quietSince = FrameChangeTracker.None;
    }

    /// <summary>How long (in the caller's timestamp units) a finished seek may show no new frame before a step is taken.</summary>
    public long Delay { get; }

    public readonly bool IsArmed => _armed;

    /// <summary>The target the armed seek asked for.</summary>
    public readonly TimeSpan Target => _target;

    /// <summary>Where a seek step must seek to: the target, <see cref="SeekRecoveryStep.SeekAway"/> 200 ms away from it.</summary>
    public readonly TimeSpan TargetFor(SeekRecoveryStep step) => step switch
    {
        SeekRecoveryStep.SeekAway => _target >= Away ? _target - Away : _target + Away,
        _ => _target,
    };

    /// <summary>Forgets everything (a file was opened or closed).</summary>
    public void Reset()
    {
        _armed = false;
        _ignoreNewFrame = false;
        _quietSince = FrameChangeTracker.None;
        _attempts = 0;
        _unsettled = 0;
        _lastStep = SeekRecoveryStep.None;
        _healedBy = SeekRecoveryStep.None;
    }

    /// <summary>A seek to <paramref name="target"/> was requested while the engine presented <paramref name="presentedPts"/>.</summary>
    public void Arm(TimeSpan target, long presentedPts)
    {
        Reset();
        _armed = true;
        _target = target;
        _baselinePts = presentedPts;
    }

    /// <summary>The picture is moving on its own (Play) or is no longer of interest.</summary>
    public void Disarm() => _armed = false;

    /// <summary>
    /// The step after which the seek turned out to work (once; <see cref="SeekRecoveryStep.None"/> when no step was taken
    /// before it worked, or nothing happened since the last call).
    /// </summary>
    public SeekRecoveryStep TakeHealedBy()
    {
        var by = _healedBy;
        _healedBy = SeekRecoveryStep.None;
        return by;
    }

    /// <summary>
    /// True when the seek is done (<paramref name="seekBusy"/> false), did not show its frame, and that has lasted
    /// <see cref="Delay"/>: the caller then reads the engine state and calls <see cref="Take"/>. Cheap: no engine calls.
    /// <paramref name="framePeriod"/> (100 ns, 0 = unknown) is the length of a frame.
    /// </summary>
    public bool IsDue(long now, bool seekBusy, long presentedPts, long framePeriod = 0)
    {
        if (!_armed)
        {
            return false;
        }

        if (Covers(presentedPts, framePeriod))
        {
            Succeeded(); // the target's own frame is up, whenever it arrived
            return false;
        }

        if (seekBusy)
        {
            // Whatever is presented now belongs to an older target (two quick clicks: the first one's frame).
            _baselinePts = presentedPts;
            _quietSince = FrameChangeTracker.None;
            return false;
        }

        if (!_ignoreNewFrame && presentedPts != _baselinePts && presentedPts != FrameChangeTracker.None)
        {
            Succeeded(); // a new frame was presented after the last seek was done
            return false;
        }

        if (_quietSince == FrameChangeTracker.None)
        {
            _quietSince = now;
            return false;
        }

        return now - _quietSince >= Delay;
    }

    /// <summary>
    /// Takes the next step after <see cref="IsDue"/> said true. An engine that is not <paramref name="engineSettled"/>
    /// (playing, seeking, no data yet) costs no attempt: the wait starts over, up to <see cref="MaxUnsettled"/> times.
    /// After the last step one more wait is given to show its effect, then the recovery gives up (None, disarmed).
    /// </summary>
    public SeekRecoveryStep Take(bool engineSettled, long now)
    {
        if (!_armed)
        {
            return SeekRecoveryStep.None;
        }

        if (!engineSettled)
        {
            _quietSince = now;
            if (++_unsettled >= MaxUnsettled)
            {
                _armed = false;
            }

            return SeekRecoveryStep.None;
        }

        if (_attempts >= MaxAttempts)
        {
            _armed = false;
            return SeekRecoveryStep.None;
        }

        _attempts++;
        _quietSince = FrameChangeTracker.None;
        _lastStep = (SeekRecoveryStep)_attempts;
        _ignoreNewFrame = _lastStep is SeekRecoveryStep.SeekAway or SeekRecoveryStep.FrameStep; // their frame is a detour
        return _lastStep;
    }

    private readonly bool Covers(long presentedPts, long framePeriod) =>
        presentedPts != FrameChangeTracker.None && presentedPts <= _target.Ticks
        && _target.Ticks - presentedPts < (framePeriod > 0 ? framePeriod : DefaultFramePeriod);

    private void Succeeded()
    {
        _armed = false;
        if (_attempts > 0)
        {
            _healedBy = _lastStep;
        }
    }
}

/// <summary>What the D3D11 hardware of this machine offers the player, probed once per process.</summary>
internal sealed record GpuCapability(bool DeviceCreated, bool SoftwareRenderer, bool VideoSupport, string Description)
{
    /// <summary>Usable for <see cref="VideoOutput.Direct3D"/>: a real GPU with video support.</summary>
    public bool IsHardwareVideo => DeviceCreated && !SoftwareRenderer && VideoSupport;
}
