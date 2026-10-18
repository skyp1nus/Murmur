using Murmur.Transcription.Native;

namespace Murmur.Transcription.Tests;

/// <summary>
/// Pins the managed mirrors of the sherpa-onnx C structs to c-api.h v1.13.8. Expected values were printed by a C
/// program (sizeof/offsetof) compiled with clang 18 against that header on x64 Linux; LLP64 (Windows x64) gives the
/// same numbers because the structs contain only pointers, int32_t and float.
/// </summary>
public sealed unsafe class NativeLayoutTests
{
    private static void Require64Bit() =>
        Assert.SkipUnless(IntPtr.Size == 8, "layout expectations were recorded for 64-bit targets only");

    [Fact]
    public void StructSizes_MatchCHeader()
    {
        Require64Bit();
        Assert.Equal(8, sizeof(SherpaOnnxFeatureConfig));
        Assert.Equal(24, sizeof(SherpaOnnxOfflineTransducerModelConfig));
        Assert.Equal(8, sizeof(SherpaOnnxOfflineSingleModelConfig));
        Assert.Equal(48, sizeof(SherpaOnnxOfflineWhisperModelConfig));
        Assert.Equal(24, sizeof(SherpaOnnxOfflineSenseVoiceModelConfig));
        Assert.Equal(40, sizeof(SherpaOnnxOfflineMoonshineModelConfig));
        Assert.Equal(16, sizeof(SherpaOnnxOfflineFireRedAsrModelConfig));
        Assert.Equal(40, sizeof(SherpaOnnxOfflineCanaryModelConfig));
        Assert.Equal(88, sizeof(SherpaOnnxOfflineFunAsrNanoModelConfig));
        Assert.Equal(64, sizeof(SherpaOnnxOfflineQwen3AsrModelConfig));
        Assert.Equal(32, sizeof(SherpaOnnxOfflineCohereTranscribeModelConfig));
        Assert.Equal(504, sizeof(SherpaOnnxOfflineModelConfig));
        Assert.Equal(16, sizeof(SherpaOnnxOfflineLMConfig));
        Assert.Equal(24, sizeof(SherpaOnnxHomophoneReplacerConfig));
        Assert.Equal(608, sizeof(SherpaOnnxOfflineRecognizerConfig));
        Assert.Equal(128, sizeof(SherpaOnnxOfflineRecognizerResult));
        Assert.Equal(32, sizeof(SherpaOnnxSileroVadModelConfig));
        Assert.Equal(32, sizeof(SherpaOnnxTenVadModelConfig));
        Assert.Equal(88, sizeof(SherpaOnnxVadModelConfig));
        Assert.Equal(24, sizeof(SherpaOnnxSpeechSegment));
    }

    [Fact]
    public void OfflineModelConfig_FieldOffsets_MatchCHeader()
    {
        Require64Bit();
        SherpaOnnxOfflineModelConfig m = default;
        var b = (byte*)&m;
        Assert.Equal(0, (byte*)&m.Transducer - b);
        Assert.Equal(24, (byte*)&m.Paraformer - b);
        Assert.Equal(32, (byte*)&m.NemoCtc - b);
        Assert.Equal(40, (byte*)&m.Whisper - b);
        Assert.Equal(88, (byte*)&m.Tdnn - b);
        Assert.Equal(96, (byte*)&m.Tokens - b);
        Assert.Equal(104, (byte*)&m.NumThreads - b);
        Assert.Equal(108, (byte*)&m.Debug - b);
        Assert.Equal(112, (byte*)&m.Provider - b);
        Assert.Equal(120, (byte*)&m.ModelType - b);
        Assert.Equal(128, (byte*)&m.ModelingUnit - b);
        Assert.Equal(136, (byte*)&m.BpeVocab - b);
        Assert.Equal(144, (byte*)&m.TeleSpeechCtc - b);
        Assert.Equal(152, (byte*)&m.SenseVoice - b);
        Assert.Equal(176, (byte*)&m.Moonshine - b);
        Assert.Equal(216, (byte*)&m.FireRedAsr - b);
        Assert.Equal(232, (byte*)&m.Dolphin - b);
        Assert.Equal(240, (byte*)&m.ZipformerCtc - b);
        Assert.Equal(248, (byte*)&m.Canary - b);
        Assert.Equal(288, (byte*)&m.WenetCtc - b);
        Assert.Equal(296, (byte*)&m.Omnilingual - b);
        Assert.Equal(304, (byte*)&m.MedAsr - b);
        Assert.Equal(312, (byte*)&m.FunAsrNano - b);
        Assert.Equal(400, (byte*)&m.FireRedAsrCtc - b);
        Assert.Equal(408, (byte*)&m.Qwen3Asr - b);
        Assert.Equal(472, (byte*)&m.CohereTranscribe - b);
    }

    [Fact]
    public void RecognizerConfig_FieldOffsets_MatchCHeader()
    {
        Require64Bit();
        SherpaOnnxOfflineRecognizerConfig c = default;
        var b = (byte*)&c;
        Assert.Equal(0, (byte*)&c.FeatConfig - b);
        Assert.Equal(8, (byte*)&c.ModelConfig - b);
        Assert.Equal(512, (byte*)&c.LmConfig - b);
        Assert.Equal(528, (byte*)&c.DecodingMethod - b);
        Assert.Equal(536, (byte*)&c.MaxActivePaths - b);
        Assert.Equal(544, (byte*)&c.HotwordsFile - b);
        Assert.Equal(552, (byte*)&c.HotwordsScore - b);
        Assert.Equal(560, (byte*)&c.RuleFsts - b);
        Assert.Equal(568, (byte*)&c.RuleFars - b);
        Assert.Equal(576, (byte*)&c.BlankPenalty - b);
        Assert.Equal(584, (byte*)&c.Hr - b);
    }

    [Fact]
    public void RecognizerResult_FieldOffsets_MatchCHeader()
    {
        Require64Bit();
        SherpaOnnxOfflineRecognizerResult r = default;
        var b = (byte*)&r;
        Assert.Equal(0, (byte*)&r.Text - b);
        Assert.Equal(8, (byte*)&r.Timestamps - b);
        Assert.Equal(16, (byte*)&r.Count - b);
        Assert.Equal(24, (byte*)&r.Tokens - b);
        Assert.Equal(32, (byte*)&r.TokensArr - b);
        Assert.Equal(40, (byte*)&r.Json - b);
        Assert.Equal(48, (byte*)&r.Lang - b);
        Assert.Equal(56, (byte*)&r.Emotion - b);
        Assert.Equal(64, (byte*)&r.Event - b);
        Assert.Equal(72, (byte*)&r.Durations - b); // the managed binding of the NuGet package reads tokens_arr here
        Assert.Equal(80, (byte*)&r.YsLogProbs - b);
        Assert.Equal(88, (byte*)&r.SegmentTimestamps - b);
        Assert.Equal(96, (byte*)&r.SegmentDurations - b);
        Assert.Equal(104, (byte*)&r.SegmentTexts - b);
        Assert.Equal(112, (byte*)&r.SegmentTextsArr - b);
        Assert.Equal(120, (byte*)&r.SegmentCount - b);
    }

    [Fact]
    public void VadStructs_FieldOffsets_MatchCHeader()
    {
        Require64Bit();
        SherpaOnnxSileroVadModelConfig s = default;
        var sb = (byte*)&s;
        Assert.Equal(0, (byte*)&s.Model - sb);
        Assert.Equal(8, (byte*)&s.Threshold - sb);
        Assert.Equal(12, (byte*)&s.MinSilenceDuration - sb);
        Assert.Equal(16, (byte*)&s.MinSpeechDuration - sb);
        Assert.Equal(20, (byte*)&s.WindowSize - sb);
        Assert.Equal(24, (byte*)&s.MaxSpeechDuration - sb);

        SherpaOnnxVadModelConfig v = default;
        var vb = (byte*)&v;
        Assert.Equal(0, (byte*)&v.SileroVad - vb);
        Assert.Equal(32, (byte*)&v.SampleRate - vb);
        Assert.Equal(36, (byte*)&v.NumThreads - vb);
        Assert.Equal(40, (byte*)&v.Provider - vb);
        Assert.Equal(48, (byte*)&v.Debug - vb);
        Assert.Equal(56, (byte*)&v.TenVad - vb);

        SherpaOnnxSpeechSegment g = default;
        var gb = (byte*)&g;
        Assert.Equal(0, (byte*)&g.Start - gb);
        Assert.Equal(8, (byte*)&g.Samples - gb);
        Assert.Equal(16, (byte*)&g.N - gb);
    }

    [Fact]
    public void NativeUtf8_RoundTripsNonAsciiStringsAndNull()
    {
        using var strings = new NativeUtf8();
        const string path = "/home/Владислав/моделі/encoder.int8.onnx";

        var p = strings.Add(path);
        Assert.True(p is not null);
        Assert.Equal(path, NativeUtf8.ToManaged(p));
        Assert.Equal((byte)0xD0, p[6]); // UTF-8 lead byte of 'В', not an ANSI code page byte
        Assert.True(strings.Add(null) is null);
        Assert.Equal(string.Empty, NativeUtf8.ToManaged(null));
    }

    [Fact]
    public void NativeLibrary_LoadsAndReportsTheExpectedVersion()
    {
        Assert.StartsWith("sherpa-onnx 1.13.8", OfflineRecognizer.NativeVersion, StringComparison.Ordinal);
    }
}
