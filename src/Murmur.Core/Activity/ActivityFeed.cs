using Murmur.Core.Models;
using Murmur.Core.Processing;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.Core.Activity;

/// <summary>
/// Aggregates what the app is busy with from the sources that already exist (no second pipeline): the recorder
/// (<see cref="RecorderState.Recording"/> / <see cref="RecorderState.Paused"/> -> one <see cref="ActivityKind.Recording"/> item),
/// the post-recording pipeline (<see cref="SessionProcessor.ProgressChanged"/> / <see cref="SessionProcessor.StatusCleared"/> -> transcribing with progress, queued, indexing) and
/// the model manager (<see cref="IModelManager.StatusChanged"/> -> downloads with progress).
/// </summary>
/// <remarks>
/// <para><b>Coalescing.</b> Every source event only stores its fact and sets a flag (<see cref="Interlocked"/>); the first event
/// after a publication arms a one-shot timer, and when it fires the snapshot is rebuilt and <see cref="Changed"/> is raised once, so
/// however fast a download reports, <see cref="Changed"/> fires at most once per interval (500 ms by default: 2 Hz). An unchanged
/// snapshot (progress compared at 1 % steps) raises nothing. The handlers allocate nothing in steady state; the snapshot (a small list) is built once per publication.</para>
/// <para>The feed does not subscribe to level meters or the recorder's frame clock: the elapsed time of a recording is read by the App
/// from <see cref="IRecorder.Elapsed"/> at its own pace.</para>
/// <para><b>Not covered.</b> <see cref="ActivityKind.Summarizing"/> is never produced: the summary of a recording runs inside the
/// session view and no Core source reports it yet.</para>
/// </remarks>
public sealed class ActivityFeed : IActivityFeed, IDisposable
{
    /// <summary>The shortest time between two <see cref="Changed"/> events: 2 Hz.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    private readonly IRecorder _recorder;
    private readonly ISessionStore? _sessions;
    private readonly SessionProcessor? _processor;
    private readonly IModelManager? _models;
    private readonly TimeSpan _interval;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, TrackedSession> _processing = [];
    private readonly Dictionary<string, TrackedDownload> _downloads = [];
    private IReadOnlyList<ActivityItem> _current = [];
    private int _pending;
    private int _disposed;

    /// <param name="recorder">The recorder of the process.</param>
    /// <param name="sessions">Names the sessions the pipeline works on (their titles); <c>null</c> = items without a title.</param>
    /// <param name="processor">The post-recording pipeline; <c>null</c> = no transcribing / indexing items.</param>
    /// <param name="models">The model manager; <c>null</c> = no download items.</param>
    /// <param name="interval">The shortest time between two <see cref="Changed"/> events; <c>null</c> = <see cref="DefaultInterval"/>.</param>
    public ActivityFeed(IRecorder recorder, ISessionStore? sessions = null, SessionProcessor? processor = null, IModelManager? models = null, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        _recorder = recorder;
        _sessions = sessions;
        _processor = processor;
        _models = models;
        _interval = interval ?? DefaultInterval;
        _timer = new Timer(static state => ((ActivityFeed)state!).Publish(), this, Timeout.Infinite, Timeout.Infinite);

        _recorder.StateChanged += OnRecorderState;
        if (_processor is not null)
        {
            _processor.ProgressChanged += OnProcessing;
            _processor.StatusCleared += OnStatusCleared;
        }

        if (_models is not null)
        {
            _models.StatusChanged += OnModelStatus;

            // A download that started before the feed was built.
            foreach (var info in _models.Catalog)
            {
                var status = _models.GetStatus(info.Id);
                if (status.State == ModelInstallState.Downloading)
                {
                    _downloads[info.Id] = new TrackedDownload("model:" + info.Id) { Status = status };
                }
            }
        }

        _current = Build();
    }

    public IReadOnlyList<ActivityItem> Current => Volatile.Read(ref _current);

    public event Action? Changed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _recorder.StateChanged -= OnRecorderState;
        if (_processor is not null)
        {
            _processor.ProgressChanged -= OnProcessing;
            _processor.StatusCleared -= OnStatusCleared;
        }

        if (_models is not null)
        {
            _models.StatusChanged -= OnModelStatus;
        }

        _timer.Dispose();
    }

    private void OnRecorderState(RecorderState state) => Mark();

    internal void OnProcessing(SessionProcessingStatus status)
    {
        var needsTitle = false;
        lock (_gate)
        {
            if (status.Stage is SessionProcessingStage.Completed or SessionProcessingStage.Failed or SessionProcessingStage.ModelMissing)
            {
                _processing.Remove(status.Session);
            }
            else if (_processing.TryGetValue(status.Session, out var tracked))
            {
                tracked.Status = status;
            }
            else
            {
                // A (re)created row reads the title afresh, so a rename since the last run shows.
                var id = status.Session.ToString();
                _processing[status.Session] = new TrackedSession("asr:" + id, "idx:" + id) { Status = status };
                needsTitle = _sessions is not null;
            }
        }

        if (needsTitle)
        {
            _ = LoadTitleAsync(status.Session);
        }

        Mark();
    }

    /// <summary>The pipeline dropped the session without a final status (it is gone): its row goes.</summary>
    internal void OnStatusCleared(SessionId session)
    {
        lock (_gate)
        {
            _processing.Remove(session);
        }

        Mark();
    }

    internal void OnModelStatus(ModelStatus status)
    {
        lock (_gate)
        {
            if (status.State != ModelInstallState.Downloading)
            {
                _downloads.Remove(status.Model.Id);
            }
            else if (_downloads.TryGetValue(status.Model.Id, out var tracked))
            {
                tracked.Status = status;
            }
            else
            {
                _downloads[status.Model.Id] = new TrackedDownload("model:" + status.Model.Id) { Status = status };
            }
        }

        Mark();
    }

    private async Task LoadTitleAsync(SessionId id)
    {
        try
        {
            var record = await _sessions!.GetSessionAsync(id, CancellationToken.None).ConfigureAwait(false);
            if (record is not null)
            {
                lock (_gate)
                {
                    // A row that finished meanwhile is not brought back to life.
                    if (_processing.TryGetValue(id, out var tracked))
                    {
                        tracked.Title = record.Title;
                    }
                }

                Mark();
            }
        }
        catch (Exception)
        {
            // The row stays without a title; a title is decoration for a fact that is already shown.
        }
    }

    /// <summary>Something changed: the first mark after a publication arms the timer, the others only find the flag set.</summary>
    private void Mark()
    {
        if (Volatile.Read(ref _disposed) == 0 && Interlocked.Exchange(ref _pending, 1) == 0)
        {
            try
            {
                _timer.Change(_interval, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Disposed between the check and the call.
            }
        }
    }

    private void Publish()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // Cleared before the rebuild: an event during it arms the next publication.
        Volatile.Write(ref _pending, 0);
        var next = Build();
        var previous = Volatile.Read(ref _current);
        if (SameItems(previous, next))
        {
            return;
        }

        Volatile.Write(ref _current, next);
        Changed?.Invoke();
    }

    private static bool SameItems(IReadOnlyList<ActivityItem> a, IReadOnlyList<ActivityItem> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly Comparison<TrackedSession> BySession = static (a, b) => a.Status.Session.Value.CompareTo(b.Status.Session.Value);

    private static readonly Comparison<TrackedDownload> ByModel = static (a, b) => string.CompareOrdinal(a.Key, b.Key);

    /// <summary>Progress at 1 % steps: a finer change is not worth a publication.</summary>
    private static double Round(double progress) => Math.Round(Math.Clamp(progress, 0, 1), 2);

    /// <summary>Running first (recording, the pipeline, downloads), then what waits; each group in a stable order.</summary>
    private List<ActivityItem> Build()
    {
        var items = new List<ActivityItem>();
        if (_recorder.State is RecorderState.Recording or RecorderState.Paused)
        {
            items.Add(new ActivityItem("rec", ActivityKind.Recording, _recorder.Current?.Title ?? string.Empty, null, false));
        }

        List<ActivityItem>? queued = null;
        bool? canIndex = null;
        lock (_gate)
        {
            if (_processing.Count > 0)
            {
                var sessions = new List<TrackedSession>(_processing.Values);
                sessions.Sort(BySession);
                foreach (var tracked in sessions)
                {
                    var status = tracked.Status;
                    switch (status.Stage)
                    {
                        case SessionProcessingStage.Queued:
                            (queued ??= []).Add(new ActivityItem(tracked.AsrKey, ActivityKind.Transcribing, tracked.Title, null, true));
                            break;
                        case SessionProcessingStage.Indexing:
                            items.Add(new ActivityItem(tracked.IndexKey, ActivityKind.Indexing, tracked.Title, null, false));
                            break;
                        default:
                            items.Add(new ActivityItem(tracked.AsrKey, ActivityKind.Transcribing, tracked.Title, Round(status.Progress), false));

                            // The indexing that follows waits for the transcript (asked only when something is transcribing).
                            if (canIndex ??= _processor?.CanIndex == true)
                            {
                                (queued ??= []).Add(new ActivityItem(tracked.IndexKey, ActivityKind.Indexing, tracked.Title, null, true));
                            }

                            break;
                    }
                }
            }

            if (_downloads.Count > 0)
            {
                var downloads = new List<TrackedDownload>(_downloads.Values);
                downloads.Sort(ByModel);
                foreach (var tracked in downloads)
                {
                    var status = tracked.Status;
                    double? progress = status.BytesTotal is > 0 ? Round((double)status.BytesDone / status.BytesTotal.Value) : null;
                    items.Add(new ActivityItem(tracked.Key, ActivityKind.DownloadingModel, status.Model.DisplayName, progress, false));
                }
            }
        }

        if (queued is not null)
        {
            items.AddRange(queued);
        }

        return items;
    }

    /// <summary>What the pipeline reports about one session, with its title and the row keys built once.</summary>
    private sealed class TrackedSession(string asrKey, string indexKey)
    {
        public string AsrKey { get; } = asrKey;

        public string IndexKey { get; } = indexKey;

        public SessionProcessingStatus Status { get; set; } = null!;

        public string Title { get; set; } = string.Empty;
    }

    private sealed class TrackedDownload(string key)
    {
        public string Key { get; } = key;

        public ModelStatus Status { get; set; } = null!;
    }
}
