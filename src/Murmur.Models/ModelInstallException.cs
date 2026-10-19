namespace Murmur.Models;

/// <summary>An install failed for good (retries exhausted, verification failed, no disk space, bad archive).</summary>
public sealed class ModelInstallException : Exception
{
    public ModelInstallException(string message)
        : base(message)
    {
    }

    public ModelInstallException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
