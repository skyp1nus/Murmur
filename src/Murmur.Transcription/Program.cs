namespace Murmur.Transcription;

internal static class Program
{
    /// <summary>Exit code for "invoked without a usable command".</summary>
    internal const int UsageExitCode = 2;

    internal const string Usage =
        "Murmur.Transcription - ASR worker process for Murmur.\n" +
        "This process is started by the Murmur app; it is not meant to be run by hand.\n" +
        "usage: Murmur.Transcription <command> [options]  (no commands implemented yet)";

    public static int Main(string[] args) => Run(args, Console.Error);

    internal static int Run(string[] args, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(error);
        error.WriteLine(Usage);
        return UsageExitCode;
    }
}
