using Murmur.App.Services;
using Murmur.Core.Capture;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

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
    AiServices? Ai = null);
