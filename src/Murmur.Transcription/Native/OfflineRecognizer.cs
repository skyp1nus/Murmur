namespace Murmur.Transcription.Native;

/// <summary>Parakeet TDT v3 (NeMo transducer) model files inside a model directory.</summary>
internal sealed class ParakeetModelFiles
{
    public ParakeetModelFiles(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
        Encoder = Pick(directory, "encoder");
        Decoder = Pick(directory, "decoder");
        Joiner = Pick(directory, "joiner");
        Tokens = Path.Combine(directory, "tokens.txt");
    }

    public string Directory { get; }

    public string Encoder { get; }

    public string Decoder { get; }

    public string Joiner { get; }

    public string Tokens { get; }

    public IReadOnlyList<string> MissingFiles()
    {
        var missing = new List<string>();
        foreach (var f in (ReadOnlySpan<string>)[Encoder, Decoder, Joiner, Tokens])
        {
            if (!File.Exists(f))
            {
                missing.Add(f);
            }
        }

        return missing;
    }

    // The int8 export (sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8) is the default; fall back to the fp32 names.
    private static string Pick(string directory, string name)
    {
        var int8 = Path.Combine(directory, name + ".int8.onnx");
        var fp32 = Path.Combine(directory, name + ".onnx");
        return File.Exists(int8) || !File.Exists(fp32) ? int8 : fp32;
    }
}

/// <summary>Managed copy of one recognition result: text plus per-token arrays (all of the same length).</summary>
internal sealed record RecognitionResult(string Text, string[] Tokens, float[] Timestamps, float[] Durations, string Language)
{
    public static readonly RecognitionResult Empty = new(string.Empty, [], [], [], string.Empty);
}

/// <summary>
/// Non-streaming sherpa-onnx recognizer configured for Parakeet TDT (NeMo transducer, greedy search).
/// <see cref="Decode"/> may run on several threads at once: each call uses its own streams and ONNX Runtime sessions
/// allow concurrent Run calls (sherpa-onnx's own offline websocket server decodes from a thread pool the same way);
/// RealModelTests checks that parallel and sequential decoding give identical transcripts. Dispose only when idle.
/// </summary>
internal sealed unsafe class OfflineRecognizer : IDisposable
{
    public const int SampleRate = 16_000;

    private nint _handle;

    private OfflineRecognizer(nint handle) => _handle = handle;

    public static string NativeVersion =>
        $"sherpa-onnx {NativeUtf8.ToManaged(SherpaOnnxNative.SherpaOnnxGetVersionStr())}, onnxruntime {NativeUtf8.ToManaged(SherpaOnnxNative.SherpaOnnxGetOnnxruntimeVersionStr())}";

    public static OfflineRecognizer CreateParakeet(ParakeetModelFiles model, int numThreads)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(numThreads, 1);
        var missing = model.MissingFiles();
        if (missing.Count > 0)
        {
            throw new FileNotFoundException("Parakeet model files are missing: " + string.Join(", ", missing));
        }

        using var strings = new NativeUtf8();
        SherpaOnnxOfflineRecognizerConfig config = default;
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80; // overridden by the NeMo model metadata (Parakeet uses 128 mel bins)
        config.ModelConfig.Transducer.Encoder = strings.Add(model.Encoder);
        config.ModelConfig.Transducer.Decoder = strings.Add(model.Decoder);
        config.ModelConfig.Transducer.Joiner = strings.Add(model.Joiner);
        config.ModelConfig.Tokens = strings.Add(model.Tokens);
        config.ModelConfig.NumThreads = numThreads;
        config.ModelConfig.Debug = 0;
        config.ModelConfig.Provider = strings.Add("cpu");
        config.ModelConfig.ModelType = strings.Add("nemo_transducer");
        config.DecodingMethod = strings.Add("greedy_search");
        config.MaxActivePaths = 4;

        var handle = SherpaOnnxNative.SherpaOnnxCreateOfflineRecognizer(&config);
        if (handle == 0)
        {
            throw new InvalidOperationException($"sherpa-onnx could not create the recognizer from '{model.Directory}' (see stderr for details).");
        }

        return new OfflineRecognizer(handle);
    }

    /// <summary>
    /// Decodes several independent utterances (16 kHz mono) in one call to DecodeMultipleOfflineStreams.
    /// <paramref name="results"/> receives one result per input, in the same order.
    /// </summary>
    public void Decode(ReadOnlySpan<ReadOnlyMemory<float>> utterances, Span<RecognitionResult> results)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (results.Length < utterances.Length)
        {
            throw new ArgumentException("Result span is shorter than the input.", nameof(results));
        }

        if (utterances.IsEmpty)
        {
            return;
        }

        var n = utterances.Length;
        var streams = n <= 64 ? stackalloc nint[n] : new nint[n];
        streams.Clear();
        try
        {
            for (var i = 0; i < n; i++)
            {
                var s = SherpaOnnxNative.SherpaOnnxCreateOfflineStream(_handle);
                if (s == 0)
                {
                    throw new InvalidOperationException("SherpaOnnxCreateOfflineStream returned NULL.");
                }

                streams[i] = s;
                var samples = utterances[i].Span;
                fixed (float* p = samples)
                {
                    SherpaOnnxNative.SherpaOnnxAcceptWaveformOffline(s, SampleRate, p, samples.Length);
                }
            }

            fixed (nint* ps = streams)
            {
                if (n == 1)
                {
                    SherpaOnnxNative.SherpaOnnxDecodeOfflineStream(_handle, ps[0]);
                }
                else
                {
                    SherpaOnnxNative.SherpaOnnxDecodeMultipleOfflineStreams(_handle, ps, n);
                }
            }

            for (var i = 0; i < n; i++)
            {
                results[i] = ReadResult(streams[i]);
            }
        }
        finally
        {
            foreach (var s in streams)
            {
                if (s != 0)
                {
                    SherpaOnnxNative.SherpaOnnxDestroyOfflineStream(s);
                }
            }
        }
    }

    private static RecognitionResult ReadResult(nint stream)
    {
        var r = SherpaOnnxNative.SherpaOnnxGetOfflineStreamResult(stream);
        if (r is null)
        {
            throw new InvalidOperationException("SherpaOnnxGetOfflineStreamResult returned NULL.");
        }

        try
        {
            var count = Math.Max(r->Count, 0);
            var tokens = new string[count];
            for (var i = 0; i < count; i++)
            {
                tokens[i] = r->TokensArr is null ? string.Empty : NativeUtf8.ToManaged(r->TokensArr[i]);
            }

            var timestamps = r->Timestamps is null ? [] : new ReadOnlySpan<float>(r->Timestamps, count).ToArray();
            var durations = r->Durations is null ? [] : new ReadOnlySpan<float>(r->Durations, count).ToArray();
            return new RecognitionResult(NativeUtf8.ToManaged(r->Text).Trim(), tokens, timestamps, durations, NativeUtf8.ToManaged(r->Lang));
        }
        finally
        {
            SherpaOnnxNative.SherpaOnnxDestroyOfflineRecognizerResult(r);
        }
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0)
        {
            SherpaOnnxNative.SherpaOnnxDestroyOfflineRecognizer(h);
        }
    }
}
