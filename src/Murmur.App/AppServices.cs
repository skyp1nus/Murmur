using Murmur.App.Services;
using Murmur.Core.Capture;
using Murmur.Core.Input;
using Murmur.Core.Models;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Settings;

namespace Murmur.App;

/// <summary>
/// Everything the UI needs from the outside world, built by the composition root in <c>Murmur.Desktop</c>.
/// Only Core interfaces appear here, so the app never knows which platform or storage engine it runs on.
/// </summary>
/// <param name="Recorder">The single recorder of the process.</param>
/// <param name="Capture">Used to list devices and to learn what the platform can capture.</param>
/// <param name="Sessions">Session metadata for the library.</param>
/// <param name="Time">Clock for "today" filters and date labels.</param>
/// <param name="Startup">
/// Work that must finish before the library loads (crash recovery, resuming unfinished transcriptions). Runs once;
/// the shell awaits it off the UI thread, so it may block. <c>null</c> = nothing to do.
/// </param>
/// <param name="Transcripts">Transcripts for the recording view. Together with <paramref name="Playback"/> it enables opening a session.</param>
/// <param name="Playback">Creates the audio player of the recording view.</param>
/// <param name="Processing">Post-recording pipeline status for library rows and the recording view.</param>
/// <param name="Search">BM25 search over transcripts: the library search when <paramref name="Ai"/> is missing or fails.</param>
/// <param name="Ai">Hybrid search and the "Запитати AI" chats; <c>null</c> = AI not wired (the chats explain how to install it).</param>
/// <param name="Settings">
/// User preferences. The app applies <c>Theme</c> at start-up and on every change and reads <c>Language</c> once at
/// start-up (a language change applies on the next start, decision D-032); the later settings and onboarding screens
/// save through it. <c>null</c> in designer and tests = light/system theme, Ukrainian.
/// </param>
/// <param name="Models">Downloads, installs and removes the models; the settings screen lists its catalog. <c>null</c> = no model section.</param>
/// <param name="ActiveModelId">
/// The id of the chat or embedding model that is active on disk (<c>models.json</c>), or <c>null</c>. Not part of
/// <see cref="IModelManager"/>; the composition root wires it to the model manager. <c>null</c> = the settings screen falls back
/// to <c>AppSettings.ChatModelId</c>.
/// </param>
/// <param name="DefaultRecordingsDirectory">The folder new recordings go to while <c>AppSettings.RecordingsDirectory</c> is unset; shown in the settings.</param>
/// <param name="VideoPlayback">
/// Creates the screen-recording player of the recording view (D-035). <c>null</c> or <see cref="IVideoPlayback.IsSupported"/>
/// false = no video panel; the recording view plays audio only.
/// </param>
/// <param name="Hotkeys">
/// System-wide hotkeys (record, screen, show the window). The app registers the gestures of the settings after the first
/// frame and follows their changes; the composition root owns and disposes it. <c>null</c> = no global hotkeys.
/// </param>
/// <param name="Chats">The saved chats of Ask AI (the sidebar's Chats list); <c>null</c> = chats are not saved.</param>
/// <param name="Activation">
/// Another start of the program asked this one to show its window (single instance). Raised on any thread; <c>null</c> = never.
/// </param>
public sealed record AppServices(
    IRecorder Recorder,
    ICapture Capture,
    ISessionStore Sessions,
    TimeProvider Time,
    Func<CancellationToken, Task>? Startup = null,
    ITranscriptStore? Transcripts = null,
    IAudioPlayback? Playback = null,
    ISessionProcessing? Processing = null,
    ISearchIndex? Search = null,
    AiServices? Ai = null,
    ISettingsStore? Settings = null,
    IModelManager? Models = null,
    Func<ModelKind, string?>? ActiveModelId = null,
    string? DefaultRecordingsDirectory = null,
    IVideoPlayback? VideoPlayback = null,
    IGlobalHotkeys? Hotkeys = null,
    IActivationRequests? Activation = null,
    IChatStore? Chats = null);
