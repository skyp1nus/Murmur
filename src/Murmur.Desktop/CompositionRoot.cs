using Murmur.App;
using Murmur.App.Services;
using Murmur.Core.Capture;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Jobs;
using Murmur.Core.Models;
using Murmur.Core.Processing;
using Murmur.Core.Recording;
using Murmur.Core.Settings;
using Murmur.Core.Transcription;
using Murmur.Intelligence;
using Murmur.Intelligence.Rag;
using Murmur.Models;
using Murmur.Storage;

namespace Murmur.Desktop;

/// <summary>
/// Hand-written composition root (pure DI): every service is built here with plain constructors.
/// No container, no reflection, so the object graph stays Native-AOT friendly and readable.
/// </summary>
internal sealed class CompositionRoot : IDisposable
{
    private SqliteStorage? _storage;
    private JsonSettingsStore? _settings;
    private WorkerAsrClient? _asr;
    private JobQueue? _queue;
    private SessionProcessor? _processor;
    private RecordingActivity? _activity;
    private RecordingHeapTrimmer? _heapTrimmer;
    private LocalLlm? _llm;
    private Assistant? _assistant;
    private ModelManager? _modelManager;
    private AiAwareModelManager? _aiModels;
    private AiReindexScheduler? _reindex;
    private CancellationTokenSource? _recoveryRetry;

    public CompositionRoot(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Args = args;
    }

    /// <summary>Command-line arguments the process was started with.</summary>
    public IReadOnlyList<string> Args { get; }

    public App.App CreateApp() => new(Args, CreateServices());

    /// <summary>
    /// Builds the object graph the UI works with: storage, platform capture and playback, recorder, crash recovery and
    /// the post-recording pipeline (peaks, transcription in the ASR worker process, transcript storage, indexing) and
    /// the local AI (llama-server sidecars behind the RAG assistant).
    /// </summary>
    public AppServices CreateServices()
    {
        // settings.json sits next to the database (same data folder); the app reads the language from it once at
        // start-up and follows the theme live. Storage asks it for the recordings folder at every new recording.
        var paths = StoragePaths.FromEnvironment();
        var settings = _settings ??= new JsonSettingsStore(paths);
        if (_storage is null)
        {
            _storage = new SqliteStorage(paths, () => settings.Current.RecordingsDirectory);
            _storage.RecordingsFolderFallback += static info => Console.Error.WriteLine(
                $"Recordings folder '{info.RequestedFolder}' is unusable ({info.Reason}); session {info.Session} went to the default folder.");
        }

        var storage = _storage;
        var capture = CreateCapture();
        var recorder = new Recorder(capture, storage, TimeProvider.System, RecorderSettings.Default);

        // While a recording runs the UI's short-lived garbage is collected every 30 s instead of piling up in the working
        // set until the (cache-sized, up to ~50 MB) gen0 budget runs out.
        _heapTrimmer ??= new RecordingHeapTrimmer(recorder, TimeProvider.System);

        // The processor follows the recorder: it pauses the queue (and stops the ASR worker, so no model sits in
        // memory) for as long as a recording runs, and queues every finished recording.
        var models = ModelLocator.FromEnvironment();
        var asr = _asr ??= new WorkerAsrClient(models);
        var queue = _queue ??= new JobQueue();

        // Recording guard: the models are never loaded while recording. LocalLlm refuses new requests while the
        // recorder is busy, and the moment a recording starts its llama-server processes are killed.
        var activity = _activity ??= new RecordingActivity(recorder);
        var llm = _llm ??= CreateLlm(models.ModelsDirectory, activity);
        var assistant = _assistant ??= new Assistant(llm, storage);

        // The model manager installs into the same folder the ASR and the AI read from. Its active-model lookup is handed
        // to the UI as a delegate because IModelManager does not expose it.
        var modelManager = _modelManager ??= new ModelManager(models.ModelsDirectory);

        // The UI removes models through this wrapper: it stops the llama-server first, so the files are not held open.
        var aiModels = _aiModels ??= new AiAwareModelManager(modelManager, llm, modelManager.ActiveModelId);
        var processor = _processor ??= new SessionProcessor(
            recorder,
            storage,
            storage,
            asr,
            queue,
            indexer: (session, cancellationToken) => assistant.IndexSessionAsync(session, cancellationToken).AsTask(),
            isAiInstalled: () => llm.IsInstalled);

        // AI installed in the app while it runs: sessions finished without it get their vectors now, not at the next start.
        if (_reindex is null)
        {
            var reindex = _reindex = new AiReindexScheduler(
                () => activity.IsRecording,
                cancellationToken =>
                {
                    llm.Refresh();
                    return processor.ReindexAllAsync(cancellationToken: cancellationToken).AsTask();
                },
                static ex => Console.Error.WriteLine($"Reindex after the AI install failed: {ex.Message}"));
            activity.RecordingStopped += reindex.NotifyRecordingStopped;
            modelManager.StatusChanged += status =>
            {
                // Synchronously, and before the UI's handlers (subscribed later): they poll Status in reaction to this event.
                if (status.State is ModelInstallState.Installed or ModelInstallState.NotInstalled
                    && status.Model.Kind is ModelKind.LlmRuntime or ModelKind.Chat or ModelKind.Embedding)
                {
                    llm.Refresh();
                }

                if (status.State == ModelInstallState.Installed
                    && status.Model.Kind is ModelKind.LlmRuntime or ModelKind.Embedding
                    && IsInstalled(modelManager, ModelKind.LlmRuntime)
                    && IsInstalled(modelManager, ModelKind.Embedding))
                {
                    reindex.Request();
                }
            };
        }

        // Start-up work runs before the library loads; the shell awaits it off the UI thread.
        var recovery = new RecoveryService(storage);
        return new AppServices(
            recorder,
            capture,
            storage,
            TimeProvider.System,
            async cancellationToken =>
            {
                await recovery.RecoverAsync(cancellationToken).ConfigureAwait(false);

                // Tracks still held by an antivirus/indexer right after a crash: retry just those sessions later.
                _recoveryRetry = new CancellationTokenSource();
                _ = recovery.RetryBusyInBackgroundAsync(_recoveryRetry.Token);
                await processor.ResumePendingAsync(cancellationToken).ConfigureAwait(false);

                // Sessions finished while the AI was not installed get their vectors now (a no-op without the models).
                await processor.ReindexAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            },
            storage,
            CreatePlayback(),
            new SessionProcessingAdapter(processor, models),
            storage,
            new AiServices(assistant, llm, models.ModelsDirectory),
            settings,
            aiModels,
            modelManager.ActiveModelId,
            storage.Paths.RecordingsDirectory);
    }

    public void Dispose()
    {
        _recoveryRetry?.Cancel();
        _heapTrimmer?.Dispose();
        _reindex?.Dispose();
        _processor?.Dispose();
        _queue?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _activity?.Dispose();

        // Stops the llama-server processes, so none outlives the app.
        _llm?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _asr?.DisposeAsync().AsTask().GetAwaiter().GetResult();

        // Stops running model downloads (their partial files are kept for the next start).
        _modelManager?.Dispose();
        _storage?.Dispose();
    }

    private static LocalLlm CreateLlm(string modelsDirectory, RecordingActivity activity)
    {
        // Paths are resolved again on demand (environment, manifest, defaults), so models installed, switched or removed
        // in the app are noticed without a restart.
        var llm = new LocalLlm(() => AiModelPaths.Resolve(modelsDirectory, Environment.GetEnvironmentVariable), () => activity.IsRecording);
        activity.RecordingStarted += llm.NotifyRecordingStarted;
        return llm;
    }

    private static bool IsInstalled(IModelManager manager, ModelKind kind)
    {
        foreach (var model in manager.Catalog)
        {
            if (model.Kind == kind && manager.GetStatus(model.Id).State == ModelInstallState.Installed)
            {
                return true;
            }
        }

        return false;
    }

    private static ICapture CreateCapture()
    {
#if WINDOWS
        return new Murmur.Capture.Windows.WindowsCapture();
#else
        // Linux development and CI: deterministic synthetic audio. The synthetic screen recorder only creates empty
        // segment files, which lets the screen toggle and --screen be exercised end to end.
        return new SyntheticCapture(new SyntheticCaptureOptions(Screen: true));
#endif
    }

    private static IAudioPlayback CreatePlayback()
    {
#if WINDOWS
        return new Murmur.Capture.Windows.Playback.WasapiAudioPlayback();
#else
        // Silent players driven by a clock: the recording view works end to end (position, highlight, seeking) without a sound device.
        return new ClockAudioPlayback();
#endif
    }
}
