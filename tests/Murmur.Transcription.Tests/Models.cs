using Murmur.Transcription.Audio;
using Murmur.Transcription.Native;

namespace Murmur.Transcription.Tests;

/// <summary>Locates the real models: <c>$MURMUR_MODELS_DIR</c> or <c>/root/models</c> (the night-run machine).</summary>
internal static class Models
{
    public const string ParakeetDirName = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";

    public static string Root =>
        Environment.GetEnvironmentVariable("MURMUR_MODELS_DIR") is { Length: > 0 } dir ? dir : "/root/models";

    public static string ParakeetDir => Path.Combine(Root, ParakeetDirName);

    public static string Vad => Path.Combine(Root, "silero_vad.onnx");

    public static bool Available =>
        Directory.Exists(ParakeetDir) && new ParakeetModelFiles(ParakeetDir).MissingFiles().Count == 0 && File.Exists(Vad);

    public static void SkipIfMissing() =>
        Assert.SkipUnless(Available, $"ASR models not found: need {ParakeetDir}/{{encoder,decoder,joiner}}.int8.onnx, tokens.txt and {Vad} (set MURMUR_MODELS_DIR)");

    public static string TestWav(string language) => Path.Combine(ParakeetDir, "test_wavs", language + ".wav");

    /// <summary>The model's test clip converted to 16 kHz mono (the test wavs are 22.05/24 kHz).</summary>
    public static float[] TestClip16k(string language) => WavFile.ReadAs16kMono(TestWav(language));
}

/// <summary>Real-model tests share one recognizer and never run in parallel with each other (RAM).</summary>
[CollectionDefinition(Name)]
public sealed class ModelCollection : ICollectionFixture<ModelFixture>
{
    public const string Name = "real-models";
}

public sealed class ModelFixture : IDisposable
{
    private readonly Lazy<OfflineRecognizer> _recognizer = new(() => OfflineRecognizer.CreateParakeet(new ParakeetModelFiles(Models.ParakeetDir), 1));

    internal OfflineRecognizer Recognizer => _recognizer.Value;

    public void Dispose()
    {
        if (_recognizer.IsValueCreated)
        {
            _recognizer.Value.Dispose();
        }
    }
}
