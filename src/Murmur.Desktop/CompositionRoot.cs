using Murmur.App;
using Murmur.Core.Capture;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Recording;
using Murmur.Storage;

namespace Murmur.Desktop;

/// <summary>
/// Hand-written composition root (pure DI): every service is built here with plain constructors.
/// No container, no reflection, so the object graph stays Native-AOT friendly and readable.
/// </summary>
internal sealed class CompositionRoot : IDisposable
{
    private SqliteStorage? _storage;

    public CompositionRoot(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Args = args;
    }

    /// <summary>Command-line arguments the process was started with.</summary>
    public IReadOnlyList<string> Args { get; }

    public App.App CreateApp() => new(Args, CreateServices());

    /// <summary>Builds the object graph the UI works with: storage, platform capture, recorder, crash recovery.</summary>
    public AppServices CreateServices()
    {
        var storage = _storage ??= SqliteStorage.CreateDefault();
        var capture = CreateCapture();
        var recorder = new Recorder(capture, storage, TimeProvider.System, RecorderSettings.Default);

        // Crash recovery runs before the library loads; the shell awaits it off the UI thread.
        var recovery = new RecoveryService(storage);
        return new AppServices(
            recorder,
            capture,
            storage,
            TimeProvider.System,
            async cancellationToken => await recovery.RecoverAsync(cancellationToken).ConfigureAwait(false));
    }

    public void Dispose() => _storage?.Dispose();

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
}
