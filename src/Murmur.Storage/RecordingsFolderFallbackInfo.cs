using Murmur.Core.Sessions;

namespace Murmur.Storage;

/// <summary>
/// Reported when the recordings folder from the settings could not be used and a session went to the default folder
/// instead (see <see cref="SqliteStorage.RecordingsFolderFallback"/> and <see cref="SqliteStorage.LastFallback"/>).
/// </summary>
/// <param name="Session">The session that was recorded into the default folder.</param>
/// <param name="RequestedFolder">The configured value that was refused.</param>
/// <param name="Reason">Why: not an absolute path, or the message of the I/O error.</param>
public sealed record RecordingsFolderFallbackInfo(SessionId Session, string RequestedFolder, string Reason);
