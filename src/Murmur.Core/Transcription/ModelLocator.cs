namespace Murmur.Core.Transcription;

/// <summary>
/// Where the ASR models live: <c>$MURMUR_MODELS_DIR</c>, else <c>LocalApplicationData/Murmur/models</c>.
/// The Parakeet TDT v3 int8 model sits in <see cref="ParakeetDirectoryName"/> and the Silero VAD next to it.
/// </summary>
public sealed class ModelLocator
{
    public const string ModelsDirectoryVariable = "MURMUR_MODELS_DIR";

    public const string ParakeetDirectoryName = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";

    public const string VadFileName = "silero_vad.onnx";

    /// <summary>Files that must exist inside <see cref="ParakeetDirectory"/>.</summary>
    public static IReadOnlyList<string> ParakeetFiles { get; } = ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"];

    public ModelLocator(string modelsDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelsDirectory);
        ModelsDirectory = modelsDirectory;
    }

    public string ModelsDirectory { get; }

    public string ParakeetDirectory => Path.Combine(ModelsDirectory, ParakeetDirectoryName);

    public string VadModel => Path.Combine(ModelsDirectory, VadFileName);

    /// <summary>Checks the files on disk on every call (a handful of <see cref="File.Exists"/> calls).</summary>
    public bool IsAsrInstalled => MissingAsrFiles().Count == 0;

    /// <summary>The locator the app uses: <c>$MURMUR_MODELS_DIR</c> or <see cref="DefaultModelsDirectory"/>.</summary>
    public static ModelLocator FromEnvironment() =>
        new(Environment.GetEnvironmentVariable(ModelsDirectoryVariable) is { Length: > 0 } dir ? dir : DefaultModelsDirectory());

    /// <summary><c>LocalApplicationData/Murmur/models</c>.</summary>
    public static string DefaultModelsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Murmur", "models");

    /// <summary>Full paths of the ASR files that are missing; empty when the ASR is installed.</summary>
    public IReadOnlyList<string> MissingAsrFiles()
    {
        var missing = new List<string>();
        foreach (var name in ParakeetFiles)
        {
            var path = Path.Combine(ParakeetDirectory, name);
            if (!File.Exists(path))
            {
                missing.Add(path);
            }
        }

        if (!File.Exists(VadModel))
        {
            missing.Add(VadModel);
        }

        return missing;
    }
}
