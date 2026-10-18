using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Murmur.Spike.LoopbackAsr.Asr;
using Murmur.Spike.LoopbackAsr.Audio;
using Murmur.Spike.LoopbackAsr.Capture;

// All our P/Invokes and COM calls are blittable and source-generated: forbid built-in runtime marshalling.
[assembly: DisableRuntimeMarshalling]

namespace Murmur.Spike.LoopbackAsr;

internal static class Program
{
    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        var options = CliOptions.Parse(args, out var error);
        if (options is null)
        {
            if (!string.IsNullOrEmpty(error))
            {
                Console.Error.WriteLine(error);
            }

            Console.Error.WriteLine(CliOptions.Usage);
            return string.IsNullOrEmpty(error) ? 0 : 2;
        }

        if (options.SelfTest)
        {
            return SelfTest.Run();
        }

        Console.WriteLine($"Murmur.Spike.LoopbackAsr | {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture} | " +
                          $"{(IsNativeAot ? "Native AOT" : "JIT (CoreCLR)")} | {Environment.ProcessorCount} logical CPUs");

        var model = new ParakeetModel(options.ModelDir);
        var missing = model.MissingFiles().ToArray();
        if (missing.Length > 0)
        {
            Console.Error.WriteLine($"Model files not found in '{options.ModelDir}':");
            foreach (var m in missing)
            {
                Console.Error.WriteLine($"  {m}");
            }

            Console.Error.WriteLine("Pass --model-dir or set MURMUR_MODELS_DIR.");
            return 3;
        }

        // 1. Get audio.
        float[] input16k;
        try
        {
            input16k = options.WavPath is not null ? LoadWav(options.WavPath) : CaptureLoopback(options.Seconds);
        }
        catch (PlatformNotSupportedException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or COMException)
        {
            Console.Error.WriteLine($"Could not get audio: {e.Message}");
            return 4;
        }

        if (input16k.Length == 0)
        {
            Console.Error.WriteLine("No audio.");
            return 4;
        }

        if (options.SavePath is not null)
        {
            WavFile.Write(options.SavePath, input16k, AudioConverter.AsrSampleRate, 1);
            Console.WriteLine($"Saved 16 kHz mono PCM16 WAV: {options.SavePath}");
        }

        // 2. Recognise.
        var audioSeconds = (double)input16k.Length / AudioConverter.AsrSampleRate;
        var threads = Environment.ProcessorCount;
        var loadSw = Stopwatch.StartNew();
        using var recognizer = RecognizerFactory.Create(model, threads);
        loadSw.Stop();
        Console.WriteLine($"ASR binding: {recognizer.Binding}");
        Console.WriteLine($"Model loaded in {loadSw.Elapsed.TotalMilliseconds:0} ms ({threads} threads, cpu).");

        var decodeSw = Stopwatch.StartNew();
        var result = recognizer.Decode(input16k);
        decodeSw.Stop();

        Console.WriteLine();
        Console.WriteLine("Text:");
        Console.WriteLine(result.Text.Trim());
        Console.WriteLine();
        PrintTokens(result);
        Console.WriteLine();
        var decodeSeconds = decodeSw.Elapsed.TotalSeconds;
        Console.WriteLine($"Audio: {audioSeconds:0.000} s | decode: {decodeSeconds * 1000:0} ms | RTF: {decodeSeconds / audioSeconds:0.000} | " +
                          $"peak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)} MiB");
        return 0;
    }

    // PublishAot also sets IsDynamicCodeSupported=false for JIT runs, so it cannot tell the two apart. The muxer /
    // apphost passes the deps file to CoreCLR as a runtime property; a Native AOT executable has no host and no deps file.
    private static bool IsNativeAot => AppContext.GetData("APP_CONTEXT_DEPS_FILES") is null;

    private static float[] LoadWav(string path)
    {
        var wav = WavFile.Read(path);
        Console.WriteLine($"WAV: {path} | {wav.SampleRate} Hz, {wav.Channels} ch, {wav.DurationSeconds:0.000} s");
        return AudioConverter.ToAsrInput(wav.Samples, wav.SampleRate, wav.Channels);
    }

    private static float[] CaptureLoopback(double seconds)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "WASAPI loopback capture is Windows-only. On this OS pass --wav <file> to transcribe a WAV file.");
        }

        var rec = WasapiLoopbackCapture.Capture(seconds);
        Console.WriteLine($"Captured {(double)rec.Mono.Length / rec.SampleRate:0.000} s at {rec.SampleRate} Hz ({rec.FormatDescription}); " +
                          $"silence gaps filled: {(double)rec.GapFrames / rec.SampleRate:0.000} s, silent packets: {rec.SilentPackets}, " +
                          $"discontinuities: {rec.Discontinuities}.");
        return rec.SampleRate == AudioConverter.AsrSampleRate
            ? rec.Mono
            : new SincResampler(rec.SampleRate, AudioConverter.AsrSampleRate).Process(rec.Mono);
    }

    private static void PrintTokens(AsrResult result)
    {
        if (result.Timestamps is null)
        {
            Console.WriteLine($"Tokens ({result.Tokens.Length}): {string.Join("|", result.Tokens)} (no timestamps exposed)");
            return;
        }

        Console.WriteLine($"Tokens with timestamps ({result.Tokens.Length}; start s [+duration s]):");
        var line = new System.Text.StringBuilder();
        for (var i = 0; i < result.Tokens.Length; i++)
        {
            var d = result.Durations is not null ? $"+{result.Durations[i]:0.00}" : "";
            var cell = $"{result.Timestamps[i],6:0.00}{d} '{result.Tokens[i]}'";
            if (line.Length + cell.Length > 110)
            {
                Console.WriteLine(line.ToString());
                line.Clear();
            }

            line.Append("  ").Append(cell);
        }

        if (line.Length > 0)
        {
            Console.WriteLine(line.ToString());
        }
    }
}
