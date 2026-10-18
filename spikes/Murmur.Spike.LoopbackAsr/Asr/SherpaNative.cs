using System.Runtime.InteropServices;

namespace Murmur.Spike.LoopbackAsr.Asr;

// Thin, blittable [LibraryImport] layer over sherpa-onnx's C API (sherpa-onnx/c-api/c-api.h, v1.13.8).
// Every struct mirrors the C declaration field-for-field; "const char *" is nint (UTF-8, owned by the caller).
// Being fully blittable, no runtime marshalling is involved: LibraryImport emits a direct call and the
// structs are passed by pointer. Zero-initialised fields are valid: the C API maps NULL/0 to its defaults.
// Layout is pinned by SelfTest against sizes/offsets compiled from c-api.h with clang (x64 / arm64 LP64).

[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxFeatureConfig
{
    public int SampleRate;
    public int FeatureDim;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxOfflineTransducerModelConfig
{
    public nint Encoder;
    public nint Decoder;
    public nint Joiner;
}

/// <summary>Shape shared by every single-model config (paraformer, nemo_ctc, tdnn, dolphin, zipformer_ctc, ...).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxOfflineSingleModelConfig
{
    public nint Model;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxOfflineWhisperModelConfig
{
    public nint Encoder;
    public nint Decoder;
    public nint Language;
    public nint Task;
    public int TailPaddings;
    public int EnableTokenTimestamps;
    public int EnableSegmentTimestamps;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxOfflineSenseVoiceModelConfig
{
    public nint Model;
    public nint Language;
    public int UseItn;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxOfflineMoonshineModelConfig
{
    public nint Preprocessor;
    public nint Encoder;
    public nint UncachedDecoder;
    public nint CachedDecoder;
    public nint MergedDecoder;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxOfflineFireRedAsrModelConfig
{
    public nint Encoder;
    public nint Decoder;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxOfflineCanaryModelConfig
{
    public nint Encoder;
    public nint Decoder;
    public nint SrcLang;
    public nint TgtLang;
    public int UsePnc;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxOfflineFunAsrNanoModelConfig
{
    public nint EncoderAdaptor;
    public nint Llm;
    public nint Embedding;
    public nint Tokenizer;
    public nint SystemPrompt;
    public nint UserPrompt;
    public int MaxNewTokens;
    public float Temperature;
    public float TopP;
    public int Seed;
    public nint Language;
    public int Itn;
    public nint Hotwords;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxOfflineQwen3AsrModelConfig
{
    public nint ConvFrontend;
    public nint Encoder;
    public nint Decoder;
    public nint Tokenizer;
    public int MaxTotalLen;
    public int MaxNewTokens;
    public float Temperature;
    public float TopP;
    public int Seed;
    public nint Hotwords;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxOfflineCohereTranscribeModelConfig
{
    public nint Encoder;
    public nint Decoder;
    public nint Language;
    public int UsePunct;
    public int UseItn;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxOfflineModelConfig
{
    public SherpaOnnxOfflineTransducerModelConfig Transducer;
    public SherpaOnnxOfflineSingleModelConfig Paraformer;
    public SherpaOnnxOfflineSingleModelConfig NemoCtc;
    public SherpaOnnxOfflineWhisperModelConfig Whisper;
    public SherpaOnnxOfflineSingleModelConfig Tdnn;
    public nint Tokens;
    public int NumThreads;
    public int Debug;
    public nint Provider;
    public nint ModelType;
    public nint ModelingUnit;
    public nint BpeVocab;
    public nint TeleSpeechCtc;
    public SherpaOnnxOfflineSenseVoiceModelConfig SenseVoice;
    public SherpaOnnxOfflineMoonshineModelConfig Moonshine;
    public SherpaOnnxOfflineFireRedAsrModelConfig FireRedAsr;
    public SherpaOnnxOfflineSingleModelConfig Dolphin;
    public SherpaOnnxOfflineSingleModelConfig ZipformerCtc;
    public SherpaOnnxOfflineCanaryModelConfig Canary;
    public SherpaOnnxOfflineSingleModelConfig WenetCtc;
    public SherpaOnnxOfflineSingleModelConfig Omnilingual;
    public SherpaOnnxOfflineSingleModelConfig MedAsr;
    public SherpaOnnxOfflineFunAsrNanoModelConfig FunAsrNano;
    public SherpaOnnxOfflineSingleModelConfig FireRedAsrCtc;
    public SherpaOnnxOfflineQwen3AsrModelConfig Qwen3Asr;
    public SherpaOnnxOfflineCohereTranscribeModelConfig CohereTranscribe;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxOfflineLMConfig
{
    public nint Model;
    public float Scale;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxHomophoneReplacerConfig
{
    public nint DictDir;
    public nint Lexicon;
    public nint RuleFsts;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxOfflineRecognizerConfig
{
    public SherpaOnnxFeatureConfig FeatConfig;
    public SherpaOnnxOfflineModelConfig ModelConfig;
    public SherpaOnnxOfflineLMConfig LmConfig;
    public nint DecodingMethod;
    public int MaxActivePaths;
    public nint HotwordsFile;
    public float HotwordsScore;
    public nint RuleFsts;
    public nint RuleFars;
    public float BlankPenalty;
    public SherpaOnnxHomophoneReplacerConfig Hr;
}

/// <summary>Read-only view of the native result (owned by sherpa-onnx; free with DestroyOfflineRecognizerResult).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxOfflineRecognizerResult
{
    public byte* Text;
    public float* Timestamps;
    public int Count;
    public byte* Tokens;
    public byte** TokensArr;
    public byte* Json;
    public byte* Lang;
    public byte* Emotion;
    public byte* Event;
    public float* Durations;
    public float* YsLogProbs;
    public float* SegmentTimestamps;
    public float* SegmentDurations;
    public byte* SegmentTexts;
    public byte** SegmentTextsArr;
    public int SegmentCount;
}

internal static unsafe partial class SherpaNative
{
    // Resolved as libsherpa-onnx-c-api.so / sherpa-onnx-c-api.dll / libsherpa-onnx-c-api.dylib next to the app
    // (shipped by the org.k2fsa.sherpa.onnx.runtime.<rid> packages).
    private const string Lib = "sherpa-onnx-c-api";

    [LibraryImport(Lib)]
    public static partial byte* SherpaOnnxGetVersionStr();

    [LibraryImport(Lib)]
    public static partial nint SherpaOnnxCreateOfflineRecognizer(SherpaOnnxOfflineRecognizerConfig* config);

    [LibraryImport(Lib)]
    public static partial void SherpaOnnxDestroyOfflineRecognizer(nint recognizer);

    [LibraryImport(Lib)]
    public static partial nint SherpaOnnxCreateOfflineStream(nint recognizer);

    [LibraryImport(Lib)]
    public static partial void SherpaOnnxDestroyOfflineStream(nint stream);

    [LibraryImport(Lib)]
    public static partial void SherpaOnnxAcceptWaveformOffline(nint stream, int sampleRate, float* samples, int n);

    [LibraryImport(Lib)]
    public static partial void SherpaOnnxDecodeOfflineStream(nint recognizer, nint stream);

    [LibraryImport(Lib)]
    public static partial SherpaOnnxOfflineRecognizerResult* SherpaOnnxGetOfflineStreamResult(nint stream);

    [LibraryImport(Lib)]
    public static partial void SherpaOnnxDestroyOfflineRecognizerResult(SherpaOnnxOfflineRecognizerResult* result);
}
