using Murmur.App;
using Murmur.App.Services;
using Murmur.Core.Capture;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Jobs;
using Murmur.Core.Processing;
using Murmur.Core.Recording;
using Murmur.Core.Transcription;
using Murmur.Storage;

namespace Murmur.Desktop;

/// <summary>
/// Hand-written composition root (pure DI): every service is built here with plain constructors.
/// No container, no reflection, so the object graph stays Native-AOT friendly and readable.
/// </summary>
internal sealed class CompositionRoot : IDisposable
{
    private SqliteStorage? _storage;
    private WorkerAsrClient? _asr;
    private JobQueue? _queue;
    private SessionProcessor? _processor;

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
    /// the post-recording pipeline (peaks, transcription in the ASR worker process, transcript storage).
    /// </summary>
    public AppServices CreateServices()
    {
        var storage = _storage ??= SqliteStorage.CreateDefault();
        var capture = CreateCapture();
        var recorder = new Recorder(capture, storage, TimeProvider.System, RecorderSettings.Default);

        // The processor follows the recorder: it pauses the queue (and stops the ASR worker, so no model sits in
        // memory) for as long as a recording runs, and queues every finished recording.
        var models = ModelLocator.FromEnvironment();
        var asr = _asr ??= new WorkerAsrClient(models);
        var queue = _queue ??= new JobQueue();
        var processor = _processor ??= new SessionProcessor(recorder, storage, storage, asr, queue);

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
                await processor.ResumePendingAsync(cancellationToken).ConfigureAwait(false);
            },
            storage,
            CreatePlayback(),
            new SessionProcessingAdapter(processor, models));
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _queue?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _asr?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _storage?.Dispose();
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
