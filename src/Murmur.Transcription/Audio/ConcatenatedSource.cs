namespace Murmur.Transcription.Audio;

/// <summary>
/// Synthesises a long 16 kHz signal on the fly: the clips in order, round-robin, each followed by a deterministic
/// 0.5–1.5 s silence, cut at exactly <c>totalSamples</c>. Nothing is materialised, so a 1-hour bench input costs
/// no memory beyond the clips themselves.
/// </summary>
internal sealed class ConcatenatedSource : ISampleSource
{
    public const int SampleRate = 16_000;
    private const int MinSilence = SampleRate / 2;
    private const int MaxSilence = SampleRate * 3 / 2;

    private readonly float[][] _clips;
    private uint _rng;
    private long _position;
    private int _clip;
    private int _offset;      // inside the current clip, or inside the silence after it
    private bool _inSilence;
    private int _silenceLength;

    public ConcatenatedSource(IReadOnlyList<float[]> clips, long totalSamples, uint seed = 0x4D75524Du)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentOutOfRangeException.ThrowIfNegative(totalSamples);
        if (clips.Count == 0)
        {
            throw new ArgumentException("At least one clip is required.", nameof(clips));
        }

        _clips = [.. clips];
        TotalSamples = totalSamples;
        _rng = seed == 0 ? 1u : seed;
    }

    public long TotalSamples { get; }

    public int Read(Span<float> destination)
    {
        var produced = 0;
        while (produced < destination.Length && _position < TotalSamples)
        {
            var room = (int)Math.Min(destination.Length - produced, TotalSamples - _position);
            int n;
            if (_inSilence)
            {
                n = Math.Min(room, _silenceLength - _offset);
                destination.Slice(produced, n).Clear();
            }
            else
            {
                var clip = _clips[_clip];
                n = Math.Min(room, clip.Length - _offset);
                clip.AsSpan(_offset, n).CopyTo(destination[produced..]);
            }

            produced += n;
            _position += n;
            _offset += n;
            Advance();
        }

        return produced;
    }

    private void Advance()
    {
        if (_inSilence)
        {
            if (_offset >= _silenceLength)
            {
                _inSilence = false;
                _offset = 0;
                _clip = (_clip + 1) % _clips.Length;
            }
        }
        else if (_offset >= _clips[_clip].Length)
        {
            _inSilence = true;
            _offset = 0;
            _silenceLength = MinSilence + (int)(NextRandom() % (uint)(MaxSilence - MinSilence + 1));
        }
    }

    // xorshift32: deterministic across runtimes and platforms (System.Random's seeded sequence is not a contract).
    private uint NextRandom()
    {
        var x = _rng;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rng = x;
        return x;
    }

    public void Dispose()
    {
    }
}
