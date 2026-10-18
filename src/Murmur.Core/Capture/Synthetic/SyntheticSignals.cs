namespace Murmur.Core.Capture.Synthetic;

/// <summary>
/// Deterministic test signals of <see cref="SyntheticCapture"/>, as pure functions of the frame index at 48 kHz.
/// </summary>
/// <remarks>
/// The system signal is index-coded: every 10 ms block (480 frames) is split into four 120-frame plateaus whose DC
/// levels carry the four bytes of the block index (XOR-scrambled so neighbouring plateaus differ). The plateaus are
/// long enough that, after the 16 kHz resampler, the centre sample of each plateau equals the plateau level, so
/// <see cref="TryDecodeSystemBlock"/> can recover the block index from a 16 kHz recording and a test can detect any
/// gap or repeat.
/// </remarks>
public static class SyntheticSignals
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    /// <summary>Frames per coded block of the system signal (10 ms).</summary>
    public const int BlockFrames = 480;

    /// <summary>Samples per coded block once resampled to 16 kHz.</summary>
    public const int BlockSamples16k = 160;

    private const int PlateauFrames = BlockFrames / 4;
    private const float MicAmplitude = 0.5f;
    private static readonly byte[] Scramble = [0xA5, 0x3C, 0x5A, 0xC3];

    public static AudioFormat Format { get; } = new(SampleRate, Channels);

    /// <summary>Microphone: 440 Hz sine, amplitude-modulated at 0.5 Hz between 0.1 and 0.5 (both channels).</summary>
    public static float Microphone(long frame)
    {
        // 440 / 48000 = 11 / 1200 and 0.5 / 48000 = 1 / 96000: exact phases from the index, no drift.
        var carrier = Math.Sin(2.0 * Math.PI * (11 * (frame % 1200)) / 1200.0);
        var envelope = 0.6 + (0.4 * Math.Sin(2.0 * Math.PI * (frame % 96_000) / 96_000.0));
        return (float)(MicAmplitude * envelope * carrier);
    }

    /// <summary>System: the index-coded plateau signal (both channels), levels in [-0.5, 0.5].</summary>
    public static float System(long frame)
    {
        var block = frame / BlockFrames;
        var plateau = (int)(frame % BlockFrames) / PlateauFrames;
        return Level(CodeByte(block, plateau));
    }

    /// <summary>Fills interleaved stereo frames starting at <paramref name="firstFrame"/>.</summary>
    public static void FillMicrophone(long firstFrame, Span<float> interleaved)
    {
        for (var i = 0; i < interleaved.Length / Channels; i++)
        {
            var v = Microphone(firstFrame + i);
            interleaved[2 * i] = v;
            interleaved[(2 * i) + 1] = v;
        }
    }

    /// <summary>Fills interleaved stereo frames starting at <paramref name="firstFrame"/>.</summary>
    public static void FillSystem(long firstFrame, Span<float> interleaved)
    {
        for (var i = 0; i < interleaved.Length / Channels; i++)
        {
            var v = System(firstFrame + i);
            interleaved[2 * i] = v;
            interleaved[(2 * i) + 1] = v;
        }
    }

    /// <summary>
    /// Decodes the block index from one block of the system signal resampled to 16 kHz mono
    /// (<see cref="BlockSamples16k"/> samples starting at a block boundary).
    /// </summary>
    public static bool TryDecodeSystemBlock(ReadOnlySpan<float> block16k, out long blockIndex)
    {
        blockIndex = -1;
        if (block16k.Length < BlockSamples16k)
        {
            return false;
        }

        long index = 0;
        const int plateauSamples = BlockSamples16k / 4;
        for (var p = 0; p < 4; p++)
        {
            var v = block16k[(p * plateauSamples) + (plateauSamples / 2)];
            var code = (v * 255.0) + 127.5;
            var rounded = Math.Round(code);
            if (Math.Abs(code - rounded) > 0.25 || rounded < 0 || rounded > 255)
            {
                return false;
            }

            index |= (long)((byte)rounded ^ Scramble[p]) << (8 * p);
        }

        blockIndex = index;
        return true;
    }

    private static byte CodeByte(long block, int plateau) => (byte)((block >> (8 * plateau)) ^ Scramble[plateau]);

    private static float Level(byte code) => (float)((code - 127.5) / 255.0);
}
