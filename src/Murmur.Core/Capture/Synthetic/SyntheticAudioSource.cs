namespace Murmur.Core.Capture.Synthetic;

/// <summary>
/// A deterministic 48 kHz stereo <see cref="IAudioSource"/>. A dedicated thread emits 10 ms buffers at real-time pace
/// times <see cref="SpeedFactor"/>, honouring the wall-clock continuity contract: after <c>t</c> seconds of (scaled)
/// time exactly <c>floor(t * 48000)</c> frames have been emitted, give or take one poll period, and
/// <see cref="Stop"/> emits everything due up to the moment it was called.
/// </summary>
public sealed class SyntheticAudioSource : IAudioSource
{
    private const int ChunkFrames = SyntheticSignals.BlockFrames;
    private const int PollMilliseconds = 2;

    private readonly TimeProvider _time;
    private readonly long _faultAfterFrames;
    private readonly long _limitFrames;
    private readonly float[] _buffer = new float[ChunkFrames * SyntheticSignals.Channels];
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly object _lifecycle = new();
    private Thread? _thread;
    private IAudioSink? _sink;
    private long _framesEmitted;
    private bool _disposed;

    /// <param name="faultAfter">Audio time after which the source raises a fatal <see cref="Faulted"/> and goes quiet.</param>
    /// <param name="audioLimit">Total audio emitted before the source goes quiet without a fault (test affordance).</param>
    public SyntheticAudioSource(AudioDevice device, double speedFactor = 1.0, TimeSpan? faultAfter = null, TimeProvider? timeProvider = null, TimeSpan? audioLimit = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!(speedFactor > 0) || double.IsInfinity(speedFactor))
        {
            throw new ArgumentOutOfRangeException(nameof(speedFactor), speedFactor, "Must be positive and finite.");
        }

        Device = device;
        SpeedFactor = speedFactor;
        _time = timeProvider ?? TimeProvider.System;
        _faultAfterFrames = faultAfter is { } f ? (long)Math.Round(f.TotalSeconds * SyntheticSignals.SampleRate) : long.MaxValue;
        _limitFrames = audioLimit is { } l ? (long)Math.Round(l.TotalSeconds * SyntheticSignals.SampleRate) : long.MaxValue;
    }

    public AudioDevice Device { get; }

    public AudioFormat Format => SyntheticSignals.Format;

    public double SpeedFactor { get; }

    /// <summary>Frames pushed to the sink since <see cref="Start"/>.</summary>
    public long FramesEmitted => Volatile.Read(ref _framesEmitted);

    public bool IsRunning => _thread is not null;

    public event Action<CaptureFault>? Faulted;

    public void Start(IAudioSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null)
            {
                throw new InvalidOperationException("The source is already started.");
            }

            _sink = sink;
            _stop.Reset();
            Volatile.Write(ref _framesEmitted, 0);
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"Synthetic audio ({Device.Kind})",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            if (_thread is null)
            {
                return;
            }

            _stop.Set();
            _thread.Join();
            _thread = null;
            _sink = null;
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_lifecycle)
        {
            if (!_disposed)
            {
                _disposed = true;
                _stop.Dispose();
            }
        }
    }

    private void Run()
    {
        var sink = _sink!;
        var start = _time.GetTimestamp();
        var rate = SyntheticSignals.SampleRate * SpeedFactor;
        var emitted = 0L;
        var system = Device.Kind == AudioDeviceKind.SystemOutput;
        var stopping = false;

        while (true)
        {
            var due = (long)(_time.GetElapsedTime(start).TotalSeconds * rate);
            due = Math.Min(due, Math.Min(_faultAfterFrames, _limitFrames));

            while (emitted < due)
            {
                var frames = (int)Math.Min(ChunkFrames, due - emitted);
                var span = _buffer.AsSpan(0, frames * SyntheticSignals.Channels);
                if (system)
                {
                    SyntheticSignals.FillSystem(emitted, span);
                }
                else
                {
                    SyntheticSignals.FillMicrophone(emitted, span);
                }

                sink.Write(span);
                emitted += frames;
                Volatile.Write(ref _framesEmitted, emitted);
            }

            if (emitted >= _faultAfterFrames)
            {
                Faulted?.Invoke(new CaptureFault(Device.Name, "Synthetic fault injected.", IsFatal: true));
                return;
            }

            if (stopping)
            {
                return;
            }

            // On Stop, one last pass emits everything due up to the stop instant, so the stream ends where the
            // caller stopped even if this thread was starved before.
            stopping = _stop.Wait(PollMilliseconds);
        }
    }
}
