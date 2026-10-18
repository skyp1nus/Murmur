using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Audio;

/// <summary>
/// The body of the capture loop, separated from the Win32 wait/thread plumbing so it runs (and is tested) on any
/// OS against a fake <see cref="IAudioCaptureClient"/>. Drains every packet, converts it to float into a
/// preallocated buffer and pushes it to the sink, keeping the stream on the wall clock via <see cref="CaptureClock"/>.
/// Hot path: no allocations, locks, logging, LINQ or async. Single-threaded (the capture thread).
/// </summary>
internal sealed class CapturePump
{
    private readonly MixFormat _format;
    private readonly CaptureClock _clock;
    private readonly float[] _buffer;
    private readonly int _chunkFrames;
    private IAudioSink? _sink;

    /// <param name="chunkFrames">Frames converted per <see cref="IAudioSink.Write"/> call (the preallocated buffer size).</param>
    public CapturePump(MixFormat format, int chunkFrames, int toleranceFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkFrames);
        _format = format;
        _chunkFrames = chunkFrames;
        _buffer = new float[chunkFrames * format.Channels];
        _clock = new CaptureClock(format.SampleRate, toleranceFrames);
    }

    public CaptureClock Clock => _clock;

    /// <summary>Packets received since <see cref="Start"/>.</summary>
    public long Packets { get; private set; }

    /// <summary>Packets flagged AUDCLNT_BUFFERFLAGS_SILENT.</summary>
    public long SilentPackets { get; private set; }

    /// <summary>Packets flagged AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY.</summary>
    public long Discontinuities { get; private set; }

    /// <summary>Silence frames inserted to cover gaps (not counting SILENT packets).</summary>
    public long GapFrames { get; private set; }

    /// <summary>Frames dropped because they overlapped frames already emitted.</summary>
    public long SkippedFrames { get; private set; }

    public void Start(IAudioSink sink, long startTicks)
    {
        _sink = sink;
        _clock.Reset(startTicks);
        Packets = SilentPackets = Discontinuities = GapFrames = SkippedFrames = 0;
    }

    /// <summary>
    /// Reads every available packet (GetBuffer until AUDCLNT_S_BUFFER_EMPTY). Returns S_OK or the first failing
    /// HRESULT (e.g. AUDCLNT_E_DEVICE_INVALIDATED); the caller then faults the source.
    /// </summary>
    public unsafe int Drain(IAudioCaptureClient client, long nowTicks)
    {
        while (true)
        {
            var hr = client.GetBuffer(out var data, out var frames, out var flags, out _, out var qpcPosition);
            if (hr == WasapiConstants.AudclntSBufferEmpty)
            {
                return WasapiConstants.SOk;
            }

            if (hr < 0)
            {
                return hr;
            }

            if (frames > 0)
            {
                var silent = (flags & WasapiConstants.AudclntBufferFlagsSilent) != 0;
                var bytes = silent || data == 0
                    ? default
                    : new ReadOnlySpan<byte>((void*)data, checked((int)frames * _format.BlockAlign));
                OnPacket(bytes, (int)frames, flags, (long)qpcPosition, nowTicks);
            }

            hr = client.ReleaseBuffer(frames);
            if (hr < 0)
            {
                return hr;
            }
        }
    }

    /// <summary>
    /// Places one packet and writes it. <paramref name="data"/> is ignored (and may be empty) when the packet is
    /// flagged SILENT; otherwise it holds <c>frames * BlockAlign</c> bytes.
    /// </summary>
    public void OnPacket(ReadOnlySpan<byte> data, int frames, uint flags, long qpcPosition, long nowTicks)
    {
        var silent = (flags & WasapiConstants.AudclntBufferFlagsSilent) != 0 || data.IsEmpty;
        var discontinuity = (flags & WasapiConstants.AudclntBufferFlagsDataDiscontinuity) != 0;
        var timestampValid = (flags & WasapiConstants.AudclntBufferFlagsTimestampError) == 0 && qpcPosition > 0;

        Packets++;
        if ((flags & WasapiConstants.AudclntBufferFlagsSilent) != 0)
        {
            SilentPackets++;
        }

        if (discontinuity)
        {
            Discontinuities++;
        }

        var placement = _clock.Place(frames, qpcPosition, timestampValid, discontinuity, nowTicks);
        if (placement.SilenceFrames > 0)
        {
            GapFrames += placement.SilenceFrames;
            EmitSilence(placement.SilenceFrames);
        }

        SkippedFrames += placement.SkipFrames;
        var remaining = frames - placement.SkipFrames;
        if (remaining <= 0)
        {
            return;
        }

        if (silent)
        {
            _sink!.WriteSilence(remaining);
            return;
        }

        var blockAlign = _format.BlockAlign;
        var channels = _format.Channels;
        var src = data.Slice(placement.SkipFrames * blockAlign, remaining * blockAlign);
        while (remaining > 0)
        {
            var n = remaining < _chunkFrames ? remaining : _chunkFrames;
            var dst = _buffer.AsSpan(0, n * channels);
            SampleConverter.ToFloat(src[..(n * blockAlign)], _format.SampleType, dst);
            _sink!.Write(dst);
            src = src[(n * blockAlign)..];
            remaining -= n;
        }
    }

    /// <summary>Fills missing frames with silence when packets stop arriving (call on every wake-up).</summary>
    public void Idle(long nowTicks)
    {
        var silence = _clock.IdleSilence(nowTicks);
        if (silence > 0)
        {
            GapFrames += silence;
            EmitSilence(silence);
        }
    }

    /// <summary>Completes the stream with silence up to <paramref name="nowTicks"/> (on stop).</summary>
    public void Finish(long nowTicks)
    {
        var silence = _clock.TailSilence(nowTicks);
        if (silence > 0)
        {
            GapFrames += silence;
            EmitSilence(silence);
        }
    }

    private void EmitSilence(long frames)
    {
        while (frames > 0)
        {
            var n = frames > int.MaxValue ? int.MaxValue : (int)frames;
            _sink!.WriteSilence(n);
            frames -= n;
        }
    }
}
