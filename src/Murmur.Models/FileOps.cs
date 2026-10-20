namespace Murmur.Models;

/// <summary>
/// Moves that survive the short-lived locks Windows puts on fresh files (antivirus, the search indexer): a failed
/// move is retried a few times, a few hundred milliseconds apart.
/// </summary>
internal static class FileOps
{
    public const int Attempts = 5;

    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(200);

    public static void MoveFile(string source, string destination, bool overwrite) =>
        Retry(() => File.Move(source, destination, overwrite), Attempts, Delay);

    public static void MoveDirectory(string source, string destination) =>
        Retry(() => Directory.Move(source, destination), Attempts, Delay);

    /// <summary>
    /// Runs <paramref name="action"/>; on <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> tries
    /// again after <paramref name="delay"/>, up to <paramref name="attempts"/> times in total. A missing source is not
    /// retried: waiting would not bring it back.
    /// </summary>
    public static void Retry(Action action, int attempts, TimeSpan delay)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < attempts && ex is (IOException or UnauthorizedAccessException) and not (FileNotFoundException or DirectoryNotFoundException))
            {
                Thread.Sleep(delay);
            }
        }
    }
}
