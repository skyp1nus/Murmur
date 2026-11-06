using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Settings;

namespace Murmur.App.Services;

/// <summary>
/// What the recording view needs besides the player, the transcript and the pipeline: the summary (writer and store), the session
/// store for renaming and deleting, and the file manager. Every part is optional: without it the matching button explains or is hidden.
/// </summary>
/// <param name="Summarizer">Writes the summary with the local model; <c>null</c> = AI not wired (the Summary tab explains how to install it).</param>
/// <param name="Summaries">Keeps the summary between runs; <c>null</c> = a summary lives only while the view is open.</param>
/// <param name="Sessions">Renames and deletes the recording; <c>null</c> = neither is offered.</param>
/// <param name="Folders">Opens the recording's folder in the file manager; <c>null</c> = "Show in folder" is not offered.</param>
/// <param name="ChapterGenerator">Splits the transcript into chapters with the local model, right after each summary; <c>null</c> = no chapters are made.</param>
/// <param name="Chapters">Keeps the chapters between visits; <c>null</c> = the strip shows none.</param>
/// <param name="SpeakerNames">Keeps the names of the two speakers of the recording; <c>null</c> = the speaker chips are not offered.</param>
/// <param name="Settings">Keeps the choices that belong to every recording (the "Follow along" switch); <c>null</c> = the choice lives only while the view is open.</param>
/// <param name="Bookmarks">Keeps the marked moments of the recording (#45); <c>null</c> = the Bookmarks card, the flags above the waveform and the pills in the transcript are not offered.</param>
/// <param name="Vocabulary">The user's own words: "Did you mean" offers them first and keeps every fix in them; <c>null</c> = the fixes are not remembered.</param>
/// <param name="WordSuggester">Asks the local model for alternatives to an uncertain word; <c>null</c> = only the vocabulary's.</param>
/// <param name="CreatePlayer">Makes a new audio player; used to bring the playback back when deleting the recording failed (the first one was released for the delete). <c>null</c> = the playback stays closed then.</param>
public sealed record SessionServices(
    ISessionSummarizer? Summarizer = null,
    ISummaryStore? Summaries = null,
    ISessionStore? Sessions = null,
    IFolderLauncher? Folders = null,
    Func<Core.Capture.IAudioPlayer>? CreatePlayer = null,
    IChapterGenerator? ChapterGenerator = null,
    IChapterStore? Chapters = null,
    ISpeakerNameStore? SpeakerNames = null,
    ISettingsStore? Settings = null,
    IBookmarkStore? Bookmarks = null,
    IVocabularyStore? Vocabulary = null,
    IWordSuggester? WordSuggester = null);
