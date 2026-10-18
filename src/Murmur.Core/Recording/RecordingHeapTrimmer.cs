namespace Murmur.Core.Recording;

/// <summary>
/// Keeps the managed heap from growing for as long as a recording runs: while the recorder is
/// <see cref="RecorderState.Recording"/> it induces a young-generation (gen0) collection every
/// <see cref="Interval"/>, but only when at least <see cref="MinimumAllocatedBytes"/> were allocated since the
/// previous check.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> A recording allocates almost nothing (the audio path is allocation-free), but the UI does: the orb's
/// composition updates and the timer cost the UI and render threads about 1-4 MB per minute of short-lived garbage.
/// The GC sizes the gen0 budget from the CPU's last-level cache, so on a CPU with a large L3 it is huge (48 MB measured
/// on a Ryzen 7 5800X3D with 96 MB of L3, versus a few MB on a typical CI runner). With so little allocated, the first
/// gen0 collection then comes only after 10-20 minutes and every garbage page stays in the working set until it does:
/// the process grew 3-5 MB per minute with zero collections, which reads as a leak and breaks the 150 MB recording
/// budget. The budget cannot be lowered from the app (<c>GCgen0size</c> is honoured only as an environment variable,
/// read before <c>Main</c>), so the recorder side collects instead.
/// </para>
/// <para>
/// <b>Cost.</b> A gen0 collection of this small heap takes well under a millisecond, twice a minute; the capture
/// rings hold seconds of audio, so a pause cannot drop frames. Nothing runs while idle and nothing ever runs on an
/// audio thread: the collection is made from a timer (thread-pool) callback.
/// </para>
/// </remarks>
public sealed class RecordingHeapTrimmer : IDisposable
{
    /// <summary>Default time between two checks while recording.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    /// <summary>Default allocation since the previous check below which no collection is induced.</summary>
    public const long DefaultMinimumAllocatedBytes = 1024 * 1024;

    private readonly IRecorder _recorder;
    private readonly TimeProvider _time;
    private readonly Func<long> _allocatedBytes;
    private readonly Action _collect;
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private long _allocatedAtLastCheck;
    private long _collections;
    private bool _disposed;

    /// <summary>Starts following <paramref name="recorder"/> (already recording = trims from now on).</summary>
    public RecordingHeapTrimmer(IRecorder recorder, TimeProvider time)
        : this(recorder, time, DefaultInterval, DefaultMinimumAllocatedBytes, static () => GC.GetTotalAllocatedBytes(precise: false), CollectYoungGeneration)
    {
    }

    /// <param name="allocatedBytes">Managed bytes allocated by the process so far (test seam).</param>
    /// <param name="collect">The collection to induce (test seam).</param>
    internal RecordingHeapTrimmer(
        IRecorder recorder,
        TimeProvider time,
        TimeSpan interval,
        long minimumAllocatedBytes,
        Func<long> allocatedBytes,
        Action collect)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(allocatedBytes);
        ArgumentNullException.ThrowIfNull(collect);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumAllocatedBytes);
        _recorder = recorder;
        _time = time;
        Interval = interval;
        MinimumAllocatedBytes = minimumAllocatedBytes;
        _allocatedBytes = allocatedBytes;
        _collect = collect;
        recorder.StateChanged += OnStateChanged;
        OnStateChanged(recorder.State);
    }

    /// <summary>Time between two checks while recording.</summary>
    public TimeSpan Interval { get; }

    /// <summary>A check induces a collection only when at least this much was allocated since the previous check.</summary>
    public long MinimumAllocatedBytes { get; }

    /// <summary>Collections induced so far (diagnostics).</summary>
    public long Collections => Interlocked.Read(ref _collections);

    /// <summary>True while the periodic check runs (for tests).</summary>
    internal bool IsArmed
    {
        get
        {
            lock (_gate)
            {
                return _timer is not null;
            }
        }
    }

    public void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timer = _timer;
            _timer = null;
        }

        _recorder.StateChanged -= OnStateChanged;
        timer?.Dispose();
    }

    /// <summary>One check, exactly what the timer runs (internal for tests).</summary>
    internal void Check()
    {
        lock (_gate)
        {
            if (_disposed || _timer is null || _recorder.State != RecorderState.Recording)
            {
                return;
            }

            var allocated = _allocatedBytes();
            if (allocated - _allocatedAtLastCheck < MinimumAllocatedBytes)
            {
                return;
            }

            _allocatedAtLastCheck = allocated;
            try
            {
                _collect();
                Interlocked.Increment(ref _collections);
            }
            catch (Exception)
            {
                // Trimming is an optimisation: a failure must never reach the timer thread (it would end the process).
            }
        }
    }

    private static void CollectYoungGeneration() =>
        GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: false);

    private void OnStateChanged(RecorderState state)
    {
        ITimer? stale = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (state == RecorderState.Recording)
            {
                if (_timer is null)
                {
                    _allocatedAtLastCheck = _allocatedBytes();
                    _timer = _time.CreateTimer(static self => ((RecordingHeapTrimmer)self!).Check(), this, Interval, Interval);
                }
            }
            else if (state == RecorderState.Idle)
            {
                stale = _timer;
                _timer = null;
            }
        }

        stale?.Dispose();
    }
}
