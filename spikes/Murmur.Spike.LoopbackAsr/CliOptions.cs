using System.Globalization;

namespace Murmur.Spike.LoopbackAsr;

internal sealed record CliOptions(double Seconds, string? WavPath, string ModelDir, string? SavePath, bool SelfTest)
{
    public const string ModelFolderName = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";

    public const string Usage =
        "Usage: Murmur.Spike.LoopbackAsr [--seconds 10] [--wav <file>] [--model-dir <dir>] [--save <out.wav>] [--self-test]\n" +
        "  --seconds N      loopback capture length in seconds (Windows only, default 10)\n" +
        "  --wav FILE       transcribe a PCM16/float32 WAV of any rate/channels instead of capturing\n" +
        "  --model-dir DIR  Parakeet TDT v3 int8 directory (default $MURMUR_MODELS_DIR/" + ModelFolderName + ",\n" +
        "                   else ~/.murmur/models/" + ModelFolderName + ")\n" +
        "  --save FILE      write the 16 kHz mono input fed to the recognizer as PCM16 WAV\n" +
        "  --self-test      run the built-in checks (WAV, resampler, capture timeline, C struct layout) and exit";

    /// <summary>Parses the command line; returns null and an error message on bad input.</summary>
    public static CliOptions? Parse(string[] args, out string? error)
    {
        error = null;
        var seconds = 10.0;
        string? wav = null;
        string? modelDir = null;
        string? save = null;
        var selfTest = false;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? Next()
            {
                if (i + 1 >= args.Length)
                {
                    return null;
                }

                i++;
                return args[i];
            }

            switch (a)
            {
                case "--seconds":
                    var s = Next();
                    if (s is null || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || seconds <= 0 || seconds > 600)
                    {
                        error = "--seconds needs a number in (0, 600].";
                        return null;
                    }

                    break;
                case "--wav":
                    wav = Next();
                    if (wav is null)
                    {
                        error = "--wav needs a file path.";
                        return null;
                    }

                    break;
                case "--model-dir":
                    modelDir = Next();
                    if (modelDir is null)
                    {
                        error = "--model-dir needs a directory.";
                        return null;
                    }

                    break;
                case "--save":
                    save = Next();
                    if (save is null)
                    {
                        error = "--save needs a file path.";
                        return null;
                    }

                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                case "-h" or "--help":
                    error = "";
                    return null;
                default:
                    error = $"Unknown argument '{a}'.";
                    return null;
            }
        }

        return new CliOptions(seconds, wav, modelDir ?? DefaultModelDir(), save, selfTest);
    }

    public static string DefaultModelDir()
    {
        var root = Environment.GetEnvironmentVariable("MURMUR_MODELS_DIR");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".murmur", "models");
        }

        return Path.Combine(root, ModelFolderName);
    }
}
