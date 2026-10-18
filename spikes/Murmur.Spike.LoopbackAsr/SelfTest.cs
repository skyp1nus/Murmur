using System.Buffers.Binary;
using Murmur.Spike.LoopbackAsr.Asr;
using Murmur.Spike.LoopbackAsr.Audio;
using Murmur.Spike.LoopbackAsr.Capture;

namespace Murmur.Spike.LoopbackAsr;

/// <summary>
/// Built-in checks for the spike's own behaviour (run with --self-test). The spike lives outside the solution's
/// test projects on purpose (throwaway code), so its checks ship with the exe and also run under Native AOT.
/// </summary>
internal static unsafe class SelfTest
{
    private static int s_failures;

    public static int Run()
    {
        s_failures = 0;
        WavRoundTrip();
        WavExtensibleFloat();
        Downmix();
        ResamplerAccuracy(48_000);
        ResamplerAccuracy(44_100);
        ResamplerAccuracy(22_050);
        ResamplerRejectsAboveNyquist();
        TimelineFillsGapsAndPads();
        TimelineConvertsPcm16AndClamps();
        SherpaStructLayout();
        WasapiStructLayout();

        Console.WriteLine(s_failures == 0 ? "SELF-TEST PASSED" : $"SELF-TEST FAILED ({s_failures})");
        return s_failures == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        if (!ok)
        {
            s_failures++;
        }
    }

    private static void WavRoundTrip()
    {
        var samples = new float[2 * 441];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = MathF.Sin(i * 0.05f) * 0.8f;
        }

        foreach (var asFloat in new[] { false, true })
        {
            var parsed = WavFile.Parse(WavFile.Encode(samples, 44_100, 2, asFloat));
            var maxErr = 0f;
            for (var i = 0; i < samples.Length; i++)
            {
                maxErr = MathF.Max(maxErr, MathF.Abs(parsed.Samples[i] - samples[i]));
            }

            // PCM16: rounding (0.5 LSB) plus the 32767 (write) vs 32768 (read) scale asymmetry (< 1 LSB).
            var tolerance = asFloat ? 0f : 2f / 32767f;
            Check(parsed.SampleRate == 44_100 && parsed.Channels == 2 && parsed.Frames == 441 && maxErr <= tolerance,
                $"WAV {(asFloat ? "float32" : "PCM16")} round trip (max error {maxErr:E2})");
        }
    }

    private static void WavExtensibleFloat()
    {
        // 48 kHz stereo float32 as WAVE_FORMAT_EXTENSIBLE (fmt chunk of 40 bytes), like a WASAPI mix-format dump.
        var file = new byte[12 + 8 + 40 + 8 + 16];
        var s = file.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)(file.Length - 8));
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 40);
        var f = s[20..];
        BinaryPrimitives.WriteUInt16LittleEndian(f, 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(f[2..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(f[4..], 48_000);
        BinaryPrimitives.WriteUInt32LittleEndian(f[8..], 48_000 * 8);
        BinaryPrimitives.WriteUInt16LittleEndian(f[12..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(f[14..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(f[16..], 22);
        BinaryPrimitives.WriteUInt16LittleEndian(f[18..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(f[20..], 3);
        new Guid("00000003-0000-0010-8000-00AA00389B71").TryWriteBytes(f[24..]);
        "data"u8.CopyTo(s[60..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[64..], 16);
        BinaryPrimitives.WriteSingleLittleEndian(s[68..], 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(s[72..], -0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(s[76..], 1f);
        BinaryPrimitives.WriteSingleLittleEndian(s[80..], 0f);

        var wav = WavFile.Parse(file);
        Check(wav.SampleRate == 48_000 && wav.Channels == 2 && wav.Frames == 2 && wav.Samples[1] == -0.25f && wav.Samples[2] == 1f,
            "WAV WAVE_FORMAT_EXTENSIBLE float32 parse");
    }

    private static void Downmix()
    {
        float[] stereo = [1f, 0f, 0.5f, -0.5f, -1f, -1f];
        var mono = new float[3];
        AudioConverter.Downmix(stereo, 2, mono);
        Check(mono[0] == 0.5f && mono[1] == 0f && mono[2] == -1f, "Downmix stereo -> mono averages channels");
    }

    private static void ResamplerAccuracy(int inRate)
    {
        const double freq = 1000.0;
        var input = new float[inRate]; // 1 s
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (float)(0.5 * Math.Sin(2 * Math.PI * freq * i / inRate));
        }

        var r = new SincResampler(inRate, 16_000);
        var output = r.Process(input);
        // Ignore the filter's edge transients.
        var margin = 200;
        double errSq = 0, refSq = 0;
        for (var n = margin; n < output.Length - margin; n++)
        {
            var expected = 0.5 * Math.Sin(2 * Math.PI * freq * n / 16_000);
            errSq += (output[n] - expected) * (output[n] - expected);
            refSq += expected * expected;
        }

        var snrDb = 10 * Math.Log10(refSq / errSq);
        Check(output.Length == 16_000 && snrDb > 60, $"Resample {inRate} -> 16000 Hz: 1 kHz sine SNR {snrDb:0.0} dB (> 60), {r.Taps} taps");
    }

    private static void ResamplerRejectsAboveNyquist()
    {
        // 12 kHz cannot be represented at 16 kHz (Nyquist 8 kHz) and must not alias down to 4 kHz.
        const int inRate = 48_000;
        var input = new float[inRate];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 12_000 * i / inRate));
        }

        var output = new SincResampler(inRate, 16_000).Process(input);
        double sq = 0;
        for (var n = 200; n < output.Length - 200; n++)
        {
            sq += output[n] * output[n];
        }

        var rms = Math.Sqrt(sq / (output.Length - 400));
        var attenuationDb = 20 * Math.Log10(0.5 / Math.Sqrt(2) / rms);
        Check(attenuationDb > 60, $"Resample 48000 -> 16000 Hz: 12 kHz tone attenuated {attenuationDb:0.0} dB (> 60)");
    }

    private static void TimelineFillsGapsAndPads()
    {
        // 1 s at 1000 Hz, mono float32, stream started at t = 5 s (100 ns units).
        const long start = 50_000_000;
        var t = new CaptureTimeline(1000, 1, PacketSampleFormat.Float32, 1.0, start);
        var packet = new byte[10 * 4];
        var ones = new float[10];
        Array.Fill(ones, 1f);
        Buffer.BlockCopy(ones, 0, packet, 0, packet.Length);

        t.Append(packet, 10, silent: false, discontinuity: false, qpc100ns: start);                // frames 0..9
        t.Append(packet, 10, silent: false, discontinuity: false, qpc100ns: start + 100_000);      // 10 ms: contiguous, no gap
        t.Append(packet, 10, silent: false, discontinuity: true, qpc100ns: start + 5_000_000);     // 500 ms: gap -> silence 20..499
        t.Append(packet, 10, silent: true, discontinuity: false, qpc100ns: start + 5_100_000);     // flagged silent
        var mono = t.Complete();

        var ok = mono.Length == 1000
                 && mono[0] == 1f && mono[19] == 1f
                 && mono[20] == 0f && mono[499] == 0f
                 && mono[500] == 1f && mono[509] == 1f
                 && mono[510] == 0f && mono[519] == 0f && mono[999] == 0f
                 && t.GapFrames == 480 + 480 && t.SilentPackets == 1 && t.Discontinuities == 1;
        Check(ok, "Capture timeline: QPC gaps filled with silence, SILENT flag zeroed, tail padded to N s");
    }

    private static void TimelineConvertsPcm16AndClamps()
    {
        var t = new CaptureTimeline(100, 2, PacketSampleFormat.Pcm16, 0.05, 0); // capacity 5 frames
        var packet = new byte[8 * 2 * 2];
        for (var f = 0; f < 8; f++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(f * 4), 16384);
            BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(f * 4 + 2), -16384 / 2);
        }

        t.Append(packet, 8, silent: false, discontinuity: false, qpc100ns: 0); // 8 frames into capacity 5
        var mono = t.Complete();
        Check(mono.Length == 5 && t.IsFull && Math.Abs(mono[0] - 0.125f) < 1e-6 && Math.Abs(mono[4] - 0.125f) < 1e-6,
            "Capture timeline: PCM16 stereo -> mono, overflow clamped to capacity");
    }

    private static void SherpaStructLayout()
    {
        // Expected values printed by a C program compiled with clang against sherpa-onnx v1.13.8 c-api.h
        // (LP64/LLP64 give the same layout here: only pointers, int32_t and float are used).
        if (IntPtr.Size != 8)
        {
            Check(false, "sherpa-onnx struct layout (only 64-bit expectations recorded)");
            return;
        }

        SherpaOnnxOfflineRecognizerConfig c = default;
        SherpaOnnxOfflineRecognizerResult r = default;
        var pc = (byte*)&c;
        var pr = (byte*)&r;
        var m = (byte*)&c.ModelConfig;

        var ok = sizeof(SherpaOnnxFeatureConfig) == 8
                 && sizeof(SherpaOnnxOfflineTransducerModelConfig) == 24
                 && sizeof(SherpaOnnxOfflineWhisperModelConfig) == 48
                 && sizeof(SherpaOnnxOfflineSenseVoiceModelConfig) == 24
                 && sizeof(SherpaOnnxOfflineMoonshineModelConfig) == 40
                 && sizeof(SherpaOnnxOfflineCanaryModelConfig) == 40
                 && sizeof(SherpaOnnxOfflineFunAsrNanoModelConfig) == 88
                 && sizeof(SherpaOnnxOfflineQwen3AsrModelConfig) == 64
                 && sizeof(SherpaOnnxOfflineCohereTranscribeModelConfig) == 32
                 && sizeof(SherpaOnnxOfflineModelConfig) == 504
                 && sizeof(SherpaOnnxOfflineLMConfig) == 16
                 && sizeof(SherpaOnnxHomophoneReplacerConfig) == 24
                 && sizeof(SherpaOnnxOfflineRecognizerConfig) == 608
                 && sizeof(SherpaOnnxOfflineRecognizerResult) == 128
                 && (byte*)&c.ModelConfig.Tokens - m == 96
                 && (byte*)&c.ModelConfig.NumThreads - m == 104
                 && (byte*)&c.ModelConfig.Provider - m == 112
                 && (byte*)&c.ModelConfig.ModelType - m == 120
                 && (byte*)&c.ModelConfig.SenseVoice - m == 152
                 && (byte*)&c.ModelConfig.Canary - m == 248
                 && (byte*)&c.ModelConfig.FunAsrNano - m == 312
                 && (byte*)&c.ModelConfig.Qwen3Asr - m == 408
                 && (byte*)&c.ModelConfig.CohereTranscribe - m == 472
                 && m - pc == 8
                 && (byte*)&c.LmConfig - pc == 512
                 && (byte*)&c.DecodingMethod - pc == 528
                 && (byte*)&c.MaxActivePaths - pc == 536
                 && (byte*)&c.BlankPenalty - pc == 576
                 && (byte*)&c.Hr - pc == 584
                 && (byte*)&r.Count - pr == 16
                 && (byte*)&r.TokensArr - pr == 32
                 && (byte*)&r.Durations - pr == 72
                 && (byte*)&r.SegmentCount - pr == 120;
        Check(ok, "sherpa-onnx C struct layout matches c-api.h v1.13.8 (sizes + key offsets)");
    }

    private static void WasapiStructLayout()
    {
        WaveFormatExtensible w = default;
        var p = (byte*)&w;
        var ok = sizeof(WaveFormatEx) == 18
                 && sizeof(WaveFormatExtensible) == 40
                 && (byte*)&w.Format.CbSize - p == 16
                 && (byte*)&w.ValidBitsPerSample - p == 18
                 && (byte*)&w.ChannelMask - p == 20
                 && (byte*)&w.SubFormat - p == 24;
        Check(ok, "WAVEFORMATEX (18 B) / WAVEFORMATEXTENSIBLE (40 B, SubFormat @24) match mmreg.h pack(1)");
    }
}
