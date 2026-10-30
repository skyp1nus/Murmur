namespace Murmur.Capture.Windows.Playback;

/// <summary>Sample-wise mixing of PCM16 tracks: plain sum, then a soft knee instead of hard clipping.</summary>
internal static class PlaybackMixer
{
    /// <summary>Samples up to this magnitude pass unchanged (0.75 of full scale).</summary>
    internal const int Knee = 24_576;

    private const float Headroom = short.MaxValue - Knee;

    /// <summary>
    /// Identity up to <see cref="Knee"/>; above it <c>Knee + (32767 - Knee) * tanh((|x| - Knee) / (32767 - Knee))</c>,
    /// which is continuous with slope 1 at the knee and approaches (never exceeds) full scale. Odd-symmetric.
    /// </summary>
    public static short SoftClip(int sum)
    {
        if (sum is >= -Knee and <= Knee)
        {
            return (short)sum;
        }

        var magnitude = Math.Abs(sum);
        var shaped = Knee + (Headroom * MathF.Tanh((magnitude - Knee) / Headroom));
        var rounded = Math.Min((int)(shaped + 0.5f), short.MaxValue);
        return (short)(sum < 0 ? -rounded : rounded);
    }

    /// <summary>Adds <paramref name="samples"/> to <paramref name="accumulator"/> (same length or shorter).</summary>
    public static void Accumulate(ReadOnlySpan<short> samples, Span<int> accumulator)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            accumulator[i] += samples[i];
        }
    }

    /// <summary>Soft-clips <paramref name="accumulator"/> into <paramref name="destination"/> (same length).</summary>
    public static void Clip(ReadOnlySpan<int> accumulator, Span<short> destination)
    {
        for (var i = 0; i < accumulator.Length; i++)
        {
            destination[i] = SoftClip(accumulator[i]);
        }
    }
}

/// <summary>
/// The tracks of one session on a common timeline: <see cref="Render"/> mixes the next frames of every track
/// (shorter tracks contribute silence after their end) until the longest track ends. One track is copied as is (no
/// clipping stage). All buffers are preallocated for <see cref="MaxFramesPerRender"/>; larger requests are served in
/// several passes, so <see cref="Render"/> never allocates. Not thread-safe: owned by the render thread.
/// </summary>
internal sealed class TrackMixer : IDisposable
{
    /// <summary>Frames mixed per pass: 100 ms at 16 kHz, larger than any shared-mode endpoint buffer we request.</summary>
    internal const int MaxFramesPerRender = 1_600;

    private readonly Pcm16TrackReader[] _tracks;
    private readonly short[] _scratch;
    private readonly int[] _accumulator;
    private long _position;
    private int _mutedMask;

    /// <summary>Takes ownership of <paramref name="tracks"/> (1..n readers of the same sample rate).</summary>
    /// <exception cref="InvalidDataException">The tracks have different sample rates.</exception>
    public TrackMixer(IReadOnlyList<Pcm16TrackReader> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            throw new ArgumentException("At least one track is required.", nameof(tracks));
        }

        _tracks = new Pcm16TrackReader[tracks.Count];
        long total = 0;
        for (var i = 0; i < _tracks.Length; i++)
        {
            _tracks[i] = tracks[i];
            if (tracks[i].SampleRate != tracks[0].SampleRate)
            {
                throw new InvalidDataException($"All tracks must share one sample rate ({tracks[0].SampleRate} Hz vs {tracks[i].SampleRate} Hz).");
            }

            total = Math.Max(total, tracks[i].FrameCount);
        }

        SampleRate = tracks[0].SampleRate;
        TotalFrames = total;
        _scratch = new short[MaxFramesPerRender];
        _accumulator = new int[MaxFramesPerRender];
    }

    public int SampleRate { get; }

    /// <summary>Frames of the longest track.</summary>
    public long TotalFrames { get; }

    /// <summary>The next frame <see cref="Render"/> produces, in [0, <see cref="TotalFrames"/>].</summary>
    public long Position => _position;

    public bool AtEnd => _position >= TotalFrames;

    /// <summary>Tracks that are silenced (bit <c>i</c> = track <c>i</c>); they are still read, so every track stays at the same position.</summary>
    public int MutedMask
    {
        get => Volatile.Read(ref _mutedMask);
        set => Volatile.Write(ref _mutedMask, value);
    }

    /// <summary>Silences or restores one track (indexes 0..31). Callable from any thread while the render thread mixes.</summary>
    public void SetMuted(int track, bool muted)
    {
        if (track is < 0 or >= 32)
        {
            return;
        }

        if (muted)
        {
            Interlocked.Or(ref _mutedMask, 1 << track);
        }
        else
        {
            Interlocked.And(ref _mutedMask, ~(1 << track));
        }
    }

    /// <summary>Repositions every track to <paramref name="frame"/>, clamped to [0, <see cref="TotalFrames"/>].</summary>
    public void Seek(long frame)
    {
        _position = Math.Clamp(frame, 0, TotalFrames);
        foreach (var track in _tracks)
        {
            track.Seek(_position);
        }
    }

    /// <summary>Mixes up to <c>destination.Length</c> frames; fewer only at the end of the longest track.</summary>
    /// <returns>Frames written to the start of <paramref name="destination"/>; 0 at the end.</returns>
    public int Render(Span<short> destination)
    {
        var frames = (int)Math.Min(destination.Length, TotalFrames - _position);
        if (frames <= 0)
        {
            return 0;
        }

        for (var done = 0; done < frames;)
        {
            var pass = Math.Min(frames - done, MaxFramesPerRender);
            var output = destination.Slice(done, pass);
            var muted = MutedMask;
            if (_tracks.Length == 1)
            {
                var read = _tracks[0].Read(output);
                output[read..].Clear();
                if ((muted & 1) != 0)
                {
                    output.Clear();
                }
            }
            else
            {
                var accumulator = _accumulator.AsSpan(0, pass);
                accumulator.Clear();
                for (var t = 0; t < _tracks.Length; t++)
                {
                    var read = _tracks[t].Read(_scratch.AsSpan(0, pass));
                    if (t >= 32 || (muted & (1 << t)) == 0)
                    {
                        PlaybackMixer.Accumulate(_scratch.AsSpan(0, read), accumulator);
                    }
                }

                PlaybackMixer.Clip(accumulator, output);
            }

            done += pass;
        }

        _position += frames;
        return frames;
    }

    public void Dispose()
    {
        foreach (var track in _tracks)
        {
            track.Dispose();
        }
    }
}
