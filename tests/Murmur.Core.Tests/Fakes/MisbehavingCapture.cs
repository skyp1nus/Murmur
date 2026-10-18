using Murmur.Core.Capture;
using Murmur.Core.Capture.Synthetic;

namespace Murmur.Core.Tests.Fakes;

/// <summary>
/// Wraps a <see cref="SyntheticCapture"/> and makes chosen operations throw, like a COM source that fails on release
/// or a screen encoder whose finalize fails.
/// </summary>
internal sealed class MisbehavingCapture(SyntheticCapture inner) : ICapture
{
    private readonly List<MisbehavingSource> _sources = [];
    private readonly List<MisbehavingScreenRecorder> _screenRecorders = [];

    /// <summary>Kinds whose <see cref="IAudioSource.Stop"/> throws (after really stopping).</summary>
    public HashSet<AudioDeviceKind> ThrowOnStop { get; } = [];

    /// <summary>Kinds whose <see cref="IDisposable.Dispose"/> throws (after really disposing).</summary>
    public HashSet<AudioDeviceKind> ThrowOnDispose { get; } = [];

    /// <summary>
    /// Kinds whose <see cref="IAudioSource.Start"/> throws the given exception without starting, like WASAPI when
    /// Windows privacy settings deny microphone access (E_ACCESSDENIED at IAudioClient.Initialize).
    /// </summary>
    public Dictionary<AudioDeviceKind, Func<Exception>> ThrowOnStart { get; } = [];

    /// <summary>Kinds whose <see cref="CreateAudioSource"/> throws the given exception (no device, endpoint unusable).</summary>
    public Dictionary<AudioDeviceKind, Func<Exception>> ThrowOnCreate { get; } = [];

    /// <summary>Makes <see cref="CreateScreenRecorder"/> throw, failing a start after the audio sources started.</summary>
    public bool ThrowOnCreateScreenRecorder { get; set; }

    /// <summary>
    /// Makes <see cref="IScreenRecorder.StopSegmentAsync"/> throw (after really stopping), like a Media Foundation
    /// finalize failing on an HRESULT.
    /// </summary>
    public bool ThrowOnStopScreenSegment { get; set; }

    /// <summary>
    /// When set, <see cref="IScreenRecorder.StopSegmentAsync"/> throws the returned exception WITHOUT stopping the inner
    /// recorder, like a cancelled stop or an HRESULT raised before the sink stops: the recorder keeps recording.
    /// </summary>
    public Func<Exception>? ThrowBeforeStopScreenSegment { get; set; }

    /// <summary>Value the screen recorders report as <see cref="ScreenRecorderStats.FramesDropped"/> while alive.</summary>
    public long ScreenFramesDropped { get; set; }

    /// <summary>How long <see cref="IScreenRecorder.DisposeAsync"/> takes after releasing (holds the stop open).</summary>
    public TimeSpan ScreenDisposeDelay { get; set; }

    public IReadOnlyList<MisbehavingScreenRecorder> ScreenRecorders
    {
        get
        {
            lock (_screenRecorders)
            {
                return [.. _screenRecorders];
            }
        }
    }

    public IReadOnlyList<MisbehavingSource> Sources
    {
        get
        {
            lock (_sources)
            {
                return [.. _sources];
            }
        }
    }

    public CaptureCapabilities Capabilities => inner.Capabilities;

    public IReadOnlyList<AudioDevice> GetAudioDevices(AudioDeviceKind kind) => inner.GetAudioDevices(kind);

    public IAudioSource CreateAudioSource(AudioDeviceKind kind, string? deviceId)
    {
        if (ThrowOnCreate.TryGetValue(kind, out var createFailure))
        {
            throw createFailure();
        }

        var source = new MisbehavingSource(this, inner.CreateAudioSource(kind, deviceId), kind);
        lock (_sources)
        {
            _sources.Add(source);
        }

        return source;
    }

    public IReadOnlyList<DisplayInfo> GetDisplays() => inner.GetDisplays();

    public IScreenRecorder CreateScreenRecorder(ScreenCaptureOptions options)
    {
        if (ThrowOnCreateScreenRecorder)
        {
            throw new InvalidOperationException("Screen recorder failed to open.");
        }

        var recorder = new MisbehavingScreenRecorder(this, inner.CreateScreenRecorder(options));
        lock (_screenRecorders)
        {
            _screenRecorders.Add(recorder);
        }

        return recorder;
    }

    internal sealed class MisbehavingSource(MisbehavingCapture owner, IAudioSource inner, AudioDeviceKind kind) : IAudioSource
    {
        private int _startCalls;
        private int _stopCalls;
        private int _disposeCalls;

        public AudioDeviceKind Kind => kind;

        public int StopCalls => Volatile.Read(ref _stopCalls);

        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public AudioDevice Device => inner.Device;

        public AudioFormat Format => inner.Format;

        public event Action<CaptureFault>? Faulted
        {
            add => inner.Faulted += value;
            remove => inner.Faulted -= value;
        }

        public int StartCalls => Volatile.Read(ref _startCalls);

        public void Start(IAudioSink sink)
        {
            Interlocked.Increment(ref _startCalls);
            if (owner.ThrowOnStart.TryGetValue(kind, out var failure))
            {
                throw failure();
            }

            inner.Start(sink);
        }

        public void Stop()
        {
            Interlocked.Increment(ref _stopCalls);
            inner.Stop();
            if (owner.ThrowOnStop.Contains(kind))
            {
                throw new InvalidOperationException($"{kind} failed to stop.");
            }
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            inner.Dispose();
            if (owner.ThrowOnDispose.Contains(kind))
            {
                throw new InvalidOperationException($"{kind} failed to release.");
            }
        }
    }

    /// <summary>
    /// Wraps a screen recorder; like a strict real one, its <see cref="Stats"/> throws
    /// <see cref="ObjectDisposedException"/> once it has been disposed.
    /// </summary>
    internal sealed class MisbehavingScreenRecorder(MisbehavingCapture owner, IScreenRecorder inner) : IScreenRecorder
    {
        private int _stopSegmentCalls;
        private int _disposeCalls;
        private volatile bool _disposed;

        public int StopSegmentCalls => Volatile.Read(ref _stopSegmentCalls);

        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public bool IsRecording => inner.IsRecording;

        public ScreenRecorderStats Stats
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return inner.Stats with { FramesDropped = owner.ScreenFramesDropped };
            }
        }

        public event Action<CaptureFault>? Faulted
        {
            add => inner.Faulted += value;
            remove => inner.Faulted -= value;
        }

        public ValueTask StartSegmentAsync(string filePath, CancellationToken cancellationToken) =>
            inner.StartSegmentAsync(filePath, cancellationToken);

        public async ValueTask StopSegmentAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stopSegmentCalls);
            if (owner.ThrowBeforeStopScreenSegment is { } failure)
            {
                throw failure();
            }

            await inner.StopSegmentAsync(cancellationToken);
            if (owner.ThrowOnStopScreenSegment)
            {
                throw new InvalidOperationException("Screen segment failed to finalize.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            await inner.DisposeAsync();
            _disposed = true;
            if (owner.ScreenDisposeDelay > TimeSpan.Zero)
            {
                await Task.Delay(owner.ScreenDisposeDelay);
            }
        }
    }
}
