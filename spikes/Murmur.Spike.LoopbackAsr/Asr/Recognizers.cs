using System.Runtime.InteropServices;
using System.Text;

namespace Murmur.Spike.LoopbackAsr.Asr;

internal sealed record AsrResult(string Text, string[] Tokens, float[]? Timestamps, float[]? Durations);

/// <summary>Parakeet TDT v3 (NeMo transducer) model files inside a model directory.</summary>
internal sealed record ParakeetModel(string Directory)
{
    public string Encoder => Path.Combine(Directory, "encoder.int8.onnx");

    public string Decoder => Path.Combine(Directory, "decoder.int8.onnx");

    public string Joiner => Path.Combine(Directory, "joiner.int8.onnx");

    public string Tokens => Path.Combine(Directory, "tokens.txt");

    public IEnumerable<string> MissingFiles()
    {
        foreach (var f in new[] { Encoder, Decoder, Joiner, Tokens })
        {
            if (!File.Exists(f))
            {
                yield return f;
            }
        }
    }
}

internal interface ISpikeRecognizer : IDisposable
{
    string Binding { get; }

    AsrResult Decode(ReadOnlySpan<float> samples16k);
}

internal static class RecognizerFactory
{
    public static ISpikeRecognizer Create(ParakeetModel model, int numThreads) =>
#if SHERPA_MANAGED
        new ManagedSherpaRecognizer(model, numThreads);
#else
        new NativeSherpaRecognizer(model, numThreads);
#endif
}

/// <summary>Offline recognizer over our own blittable [LibraryImport] layer (<see cref="SherpaNative"/>).</summary>
internal sealed unsafe class NativeSherpaRecognizer : ISpikeRecognizer
{
    private nint _recognizer;

    public NativeSherpaRecognizer(ParakeetModel model, int numThreads)
    {
        // UTF-8 copies live only for the duration of the Create call: sherpa-onnx copies them into std::string.
        var strings = new List<nint>();
        nint Utf8(string s)
        {
            var p = Marshal.StringToCoTaskMemUTF8(s);
            strings.Add(p);
            return p;
        }

        try
        {
            SherpaOnnxOfflineRecognizerConfig config = default;
            config.FeatConfig.SampleRate = 16_000;
            config.FeatConfig.FeatureDim = 80; // overridden by the NeMo model metadata (Parakeet uses 128 mels)
            config.ModelConfig.Transducer.Encoder = Utf8(model.Encoder);
            config.ModelConfig.Transducer.Decoder = Utf8(model.Decoder);
            config.ModelConfig.Transducer.Joiner = Utf8(model.Joiner);
            config.ModelConfig.Tokens = Utf8(model.Tokens);
            config.ModelConfig.NumThreads = numThreads;
            config.ModelConfig.Debug = 0;
            config.ModelConfig.Provider = Utf8("cpu");
            config.ModelConfig.ModelType = Utf8("nemo_transducer");
            config.DecodingMethod = Utf8("greedy_search");
            config.MaxActivePaths = 4;

            _recognizer = SherpaNative.SherpaOnnxCreateOfflineRecognizer(&config);
            if (_recognizer == 0)
            {
                throw new InvalidOperationException("SherpaOnnxCreateOfflineRecognizer returned NULL (bad model paths or config).");
            }
        }
        finally
        {
            foreach (var p in strings)
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
    }

    public string Binding => "native [LibraryImport] layer over sherpa-onnx C API " + VersionString();

    public static string VersionString()
    {
        var v = SherpaNative.SherpaOnnxGetVersionStr();
        return v is null ? "?" : Marshal.PtrToStringUTF8((nint)v) ?? "?";
    }

    public AsrResult Decode(ReadOnlySpan<float> samples16k)
    {
        ObjectDisposedException.ThrowIf(_recognizer == 0, this);
        var stream = SherpaNative.SherpaOnnxCreateOfflineStream(_recognizer);
        if (stream == 0)
        {
            throw new InvalidOperationException("SherpaOnnxCreateOfflineStream returned NULL.");
        }

        try
        {
            fixed (float* p = samples16k)
            {
                SherpaNative.SherpaOnnxAcceptWaveformOffline(stream, 16_000, p, samples16k.Length);
            }

            SherpaNative.SherpaOnnxDecodeOfflineStream(_recognizer, stream);

            var r = SherpaNative.SherpaOnnxGetOfflineStreamResult(stream);
            if (r is null)
            {
                throw new InvalidOperationException("SherpaOnnxGetOfflineStreamResult returned NULL.");
            }

            try
            {
                var count = r->Count;
                var text = Utf8ToString(r->Text);
                var tokens = new string[count];
                for (var i = 0; i < count; i++)
                {
                    tokens[i] = r->TokensArr is null ? "" : Utf8ToString(r->TokensArr[i]);
                }

                float[]? timestamps = r->Timestamps is null ? null : new ReadOnlySpan<float>(r->Timestamps, count).ToArray();
                float[]? durations = r->Durations is null ? null : new ReadOnlySpan<float>(r->Durations, count).ToArray();
                return new AsrResult(text, tokens, timestamps, durations);
            }
            finally
            {
                SherpaNative.SherpaOnnxDestroyOfflineRecognizerResult(r);
            }
        }
        finally
        {
            SherpaNative.SherpaOnnxDestroyOfflineStream(stream);
        }
    }

    private static string Utf8ToString(byte* s) =>
        s is null ? "" : Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(s));

    public void Dispose()
    {
        if (_recognizer != 0)
        {
            SherpaNative.SherpaOnnxDestroyOfflineRecognizer(_recognizer);
            _recognizer = 0;
        }
    }
}

#if SHERPA_MANAGED
/// <summary>Offline recognizer over the managed SherpaOnnx.* classes from the NuGet package (AOT findings only).</summary>
internal sealed class ManagedSherpaRecognizer : ISpikeRecognizer
{
    private readonly SherpaOnnx.OfflineRecognizer _recognizer;

    public ManagedSherpaRecognizer(ParakeetModel model, int numThreads)
    {
        var config = new SherpaOnnx.OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16_000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = model.Encoder;
        config.ModelConfig.Transducer.Decoder = model.Decoder;
        config.ModelConfig.Transducer.Joiner = model.Joiner;
        config.ModelConfig.Tokens = model.Tokens;
        config.ModelConfig.NumThreads = numThreads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.ModelType = "nemo_transducer";
        config.DecodingMethod = "greedy_search";
        _recognizer = new SherpaOnnx.OfflineRecognizer(config);
    }

    public string Binding => "managed SherpaOnnx.* classes (org.k2fsa.sherpa.onnx)";

    public AsrResult Decode(ReadOnlySpan<float> samples16k)
    {
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(16_000, samples16k.ToArray());
        _recognizer.Decode(stream);
        var r = stream.Result;
        // NOTE: SherpaOnnx.OfflineRecognizerResult reads "Durations" at the offset of tokens_arr (binding bug in
        // 1.13.8), so these durations are the bits of char* pointers reinterpreted as floats. Printed on purpose.
        return new AsrResult(r.Text, r.Tokens, r.Timestamps, r.Durations);
    }

    public void Dispose() => _recognizer.Dispose();
}
#endif
