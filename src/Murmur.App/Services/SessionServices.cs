using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.App.Services;

/// <summary>
/// What the recording view needs besides the player, the transcript and the pipeline: the summary (writer and store), the session
/// store for renaming and deleting, and the file manager. Every part is optional: without it the matching button explains or is hidden.
/// </summary>
/// <param name="Summarizer">Writes the summary with the local model; <c>null</c> = AI not wired (the Summary tab explains how to install it).</param>
/// <param name="Summaries">Keeps the summary between runs; <c>null</c> = a summary lives only while the view is open.</param>
/// <param name="Sessions">Renames and deletes the recording; <c>null</c> = neither is offered.</param>
/// <param name="Folders">Opens the recording's folder in the file manager; <c>null</c> = "Show in folder" is not offered.</param>
/// <param name="CreatePlayer">Makes a new audio player; used to bring the playback back when deleting the recording failed (the first one was released for the delete). <c>null</c> = the playback stays closed then.</param>
public sealed record SessionServices(
    ISessionSummarizer? Summarizer = null,
    ISummaryStore? Summaries = null,
    ISessionStore? Sessions = null,
    IFolderLauncher? Folders = null,
    Func<Core.Capture.IAudioPlayer>? CreatePlayer = null);
