namespace Murmur.Core.Transcription;

/// <summary>The ASR worker failed a job, could not be started or died.</summary>
public sealed class AsrWorkerException : Exception
{
    public AsrWorkerException()
    {
    }

    public AsrWorkerException(string message)
        : base(message)
    {
    }

    public AsrWorkerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AsrWorkerException(string message, int? exitCode, string? stderrTail, Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = exitCode;
        StderrTail = stderrTail;
    }

    /// <summary>Exit code of the worker when it exited; <c>null</c> when it is still alive or never started.</summary>
    public int? ExitCode { get; }

    /// <summary>The last lines the worker wrote to stderr, if any.</summary>
    public string? StderrTail { get; }
}

/// <summary>The ASR model files are not installed (see <see cref="ModelLocator"/>).</summary>
public sealed class AsrModelMissingException : Exception
{
    public AsrModelMissingException()
        : this([])
    {
    }

    public AsrModelMissingException(string message)
        : base(message)
    {
        MissingFiles = [];
    }

    public AsrModelMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
        MissingFiles = [];
    }

    public AsrModelMissingException(IReadOnlyList<string> missingFiles)
        : base("The Parakeet ASR model is not installed. Missing: " + string.Join(", ", missingFiles ?? []))
    {
        MissingFiles = missingFiles ?? [];
    }

    public IReadOnlyList<string> MissingFiles { get; }
}
