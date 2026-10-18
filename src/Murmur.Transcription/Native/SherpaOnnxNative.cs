using System.Runtime.InteropServices;

namespace Murmur.Transcription.Native;

// Blittable [LibraryImport] layer over the sherpa-onnx C API (sherpa-onnx/c-api/c-api.h, v1.13.8 — the version of
// the org.k2fsa.sherpa.onnx.runtime.* packages in Directory.Packages.props). Every struct mirrors the C declaration
// field for field; "const char *" is a byte* to a NUL-terminated UTF-8 string owned by the caller (see NativeUtf8).
// Zero-initialised fields are valid: the C API maps NULL / 0 to its defaults.
// Layout is pinned by NativeLayoutTests against sizes/offsets compiled from c-api.h with clang 18 (x64, LP64;
// LLP64 gives the same layout because only pointers, int32_t and float are used).
// When bumping sherpa-onnx: diff c-api.h, re-measure with clang, update the structs and the test together.

[StructLayout(LayoutKind.Sequential)]
internal struct SherpaOnnxFeatureConfig
{
    public int SampleRate;
    public int FeatureDim;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxOfflineTransducerModelConfig
{
    public byte* Encoder;
    public byte* Decoder;
    public byte* Joiner;
}

/// <summary>Shape shared by every single-model config (paraformer, nemo_ctc, tdnn, dolphin, zipformer_ctc, ...).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxOfflineSingleModelConfig
{
    public byte* Model;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxOfflineWhisperModelConfig
{
    public byte* Encoder;
    public byte* Decoder;
    public byte* Language;
    public byte* Task;
    public int TailPaddings;
    public int EnableTokenTimestamps;
    public int EnableSegmentTimestamps;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxOfflineSenseVoiceModelConfig
{
    public byte* Model;
    public byte* Language;
    public int UseItn;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxOfflineMoonshineModelConfig
{
    public byte* Preprocessor;
    public byte* Encoder;
    public byte* UncachedDecoder;
    public byte* CachedDecoder;
    public byte* MergedDecoder;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxOfflineFireRedAsrModelConfig
{
    public byte* Encoder;
    public byte* Decoder;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxOfflineCanaryModelConfig
{
    public byte* Encoder;
    public byte* Decoder;
    public byte* SrcLang;
    public byte* TgtLang;
    public int UsePnc;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxOfflineFunAsrNanoModelConfig
{
    public byte* EncoderAdaptor;
    public byte* Llm;
    public byte* Embedding;
    public byte* Tokenizer;
    public byte* SystemPrompt;
    public byte* UserPrompt;
    public int MaxNewTokens;
    public float Temperature;
    public float TopP;
    public int Seed;
    public byte* Language;
    public int Itn;
    public byte* Hotwords;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxOfflineQwen3AsrModelConfig
{
    public byte* ConvFrontend;
    public byte* Encoder;
    public byte* Decoder;
    public byte* Tokenizer;
    public int MaxTotalLen;
    public int MaxNewTokens;
    public float Temperature;
    public float TopP;
    public int Seed;
    public byte* Hotwords;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxOfflineCohereTranscribeModelConfig
{
    public byte* Encoder;
    public byte* Decoder;
    public byte* Language;
    public int UsePunct;
    public int UseItn;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxOfflineModelConfig
{
    public SherpaOnnxOfflineTransducerModelConfig Transducer;
    public SherpaOnnxOfflineSingleModelConfig Paraformer;
    public SherpaOnnxOfflineSingleModelConfig NemoCtc;
    public SherpaOnnxOfflineWhisperModelConfig Whisper;
    public SherpaOnnxOfflineSingleModelConfig Tdnn;
    public byte* Tokens;
    public int NumThreads;
    public int Debug;
    public byte* Provider;
    public byte* ModelType;
    public byte* ModelingUnit;
    public byte* BpeVocab;
    public byte* TeleSpeechCtc;
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
internal unsafe struct SherpaOnnxOfflineLMConfig
{
    public byte* Model;
    public float Scale;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxHomophoneReplacerConfig
{
    public byte* DictDir;
    public byte* Lexicon;
    public byte* RuleFsts;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxOfflineRecognizerConfig
{
    public SherpaOnnxFeatureConfig FeatConfig;
    public SherpaOnnxOfflineModelConfig ModelConfig;
    public SherpaOnnxOfflineLMConfig LmConfig;
    public byte* DecodingMethod;
    public int MaxActivePaths;
    public byte* HotwordsFile;
    public float HotwordsScore;
    public byte* RuleFsts;
    public byte* RuleFars;
    public float BlankPenalty;
    public SherpaOnnxHomophoneReplacerConfig Hr;
}

/// <summary>Read-only view of a native result (owned by sherpa-onnx; free with DestroyOfflineRecognizerResult).</summary>
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

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxSileroVadModelConfig
{
    public byte* Model;
    public float Threshold;
    public float MinSilenceDuration;
    public float MinSpeechDuration;
    public int WindowSize;
    public float MaxSpeechDuration;
}

/// <summary>Same shape as the Silero config; unused here but part of <see cref="SherpaOnnxVadModelConfig"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxTenVadModelConfig
{
    public byte* Model;
    public float Threshold;
    public float MinSilenceDuration;
    public float MinSpeechDuration;
    public int WindowSize;
    public float MaxSpeechDuration;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxVadModelConfig
{
    public SherpaOnnxSileroVadModelConfig SileroVad;
    public int SampleRate;
    public int NumThreads;
    public byte* Provider;
    public int Debug;
    public SherpaOnnxTenVadModelConfig TenVad;
}

/// <summary>A detected speech segment (owned by sherpa-onnx; free with DestroySpeechSegment).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SherpaOnnxSpeechSegment
{
    /// <summary>Start index in input samples, counted from the creation (or last reset) of the detector.</summary>
    public int Start;
    public float* Samples;
    public int N;
}

internal static unsafe partial class SherpaOnnxNative
{
    // Resolved as libsherpa-onnx-c-api.so / sherpa-onnx-c-api.dll / libsherpa-onnx-c-api.dylib from the app folder
    // (Native AOT publish) or runtimes/<rid>/native via deps.json (framework-dependent build). The library finds
    // onnxruntime next to itself (RPATH $ORIGIN on Linux, DLL search path of the app folder on Windows).
    private const string Lib = "sherpa-onnx-c-api";

    [LibraryImport(Lib)]
    public static partial byte* SherpaOnnxGetVersionStr();

    [LibraryImport(Lib)]
    public static partial byte* SherpaOnnxGetOnnxruntimeVersionStr();

    // ---- offline recognizer ----

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
    public static partial void SherpaOnnxDecodeMultipleOfflineStreams(nint recognizer, nint* streams, int n);

    [LibraryImport(Lib)]
    public static partial SherpaOnnxOfflineRecognizerResult* SherpaOnnxGetOfflineStreamResult(nint stream);

    [LibraryImport(Lib)]
    public static partial void SherpaOnnxDestroyOfflineRecognizerResult(SherpaOnnxOfflineRecognizerResult* result);

    // ---- voice activity detector ----

    [LibraryImport(Lib)]
    public static partial nint SherpaOnnxCreateVoiceActivityDetector(SherpaOnnxVadModelConfig* config, float bufferSizeInSeconds);

    [LibraryImport(Lib)]
    public static partial void SherpaOnnxDestroyVoiceActivityDetector(nint vad);

    [LibraryImport(Lib)]
    public static partial void SherpaOnnxVoiceActivityDetectorAcceptWaveform(nint vad, float* samples, int n);

    [LibraryImport(Lib)]
    public static partial int SherpaOnnxVoiceActivityDetectorEmpty(nint vad);

    [LibraryImport(Lib)]
    public static partial int SherpaOnnxVoiceActivityDetectorDetected(nint vad);

    [LibraryImport(Lib)]
    public static partial void SherpaOnnxVoiceActivityDetectorPop(nint vad);

    [LibraryImport(Lib)]
    public static partial void SherpaOnnxVoiceActivityDetectorClear(nint vad);

    [LibraryImport(Lib)]
    public static partial SherpaOnnxSpeechSegment* SherpaOnnxVoiceActivityDetectorFront(nint vad);

    [LibraryImport(Lib)]
    public static partial void SherpaOnnxDestroySpeechSegment(SherpaOnnxSpeechSegment* segment);

    [LibraryImport(Lib)]
    public static partial void SherpaOnnxVoiceActivityDetectorReset(nint vad);

    [LibraryImport(Lib)]
    public static partial void SherpaOnnxVoiceActivityDetectorFlush(nint vad);
}
