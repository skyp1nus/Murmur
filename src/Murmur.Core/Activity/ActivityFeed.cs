using Murmur.Core.Models;
using Murmur.Core.Processing;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.Core.Activity;

/// <summary>
/// Aggregates what the app is busy with from the sources that already exist (no second pipeline): the recorder
/// (<see cref="RecorderState.Recording"/> / <see cref="RecorderState.Paused"/> -> one <see cref="ActivityKind.Recording"/> item),
/// the post-recording pipeline (<see cref="SessionProcessor.ProgressChanged"/> -> transcribing with progress, queued, indexing) and
/// the model manager (<see cref="IModelManager.StatusChanged"/> -> downloads with progress).
/// </summary>
/// <remarks>
/// <para><b>Coalescing.</b> Every source event only stores its fact and sets a flag (<see cref="Interlocked"/>); the first event
/// after a publication arms a one-shot timer, and when it fires the snapshot is rebuilt and <see cref="Changed"/> is raised once, so
/// however fast a download reports, <see cref="Changed"/> fires at most once per interval (250 ms by default: 4 Hz). An unchanged
/// snapshot raises nothing. The handlers allocate nothing in steady state; the snapshot (a small list) is built once per publication.</para>
/// <para>The feed does not subscribe to level meters or the recorder's frame clock: the elapsed time of a recording is read by the App
/// from <see cref="IRecorder.Elapsed"/> at its own pace.</para>
/// <para><b>Not covered.</b> <see cref="ActivityKind.Summarizing"/> is never produced: the summary of a recording runs inside the
/// session view and no Core source reports it yet.</para>
/// </remarks>
public sealed class ActivityFeed : IActivityFeed, IDisposable
{
    /// <summary>The shortest time between two <see cref="Changed"/> events: 4 Hz.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    private readonly IRecorder _recorder;
    private readonly ISessionStore? _sessions;
    private readonly SessionProcessor? _processor;
    private readonly IModelManager? _models;
    private readonly TimeSpan _interval;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, SessionProcessingStatus> _processing = [];
    private readonly Dictionary<SessionId, string> _titles = [];
    private readonly HashSet<SessionId> _titleRequested = [];
    private readonly Dictionary<string, ModelStatus> _downloads = [];
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
                    _downloads[info.Id] = status;
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
            else
            {
                _processing[status.Session] = status;
                needsTitle = _sessions is not null && !_titles.ContainsKey(status.Session) && _titleRequested.Add(status.Session);
            }
        }

        if (needsTitle)
        {
            _ = LoadTitleAsync(status.Session);
        }

        Mark();
    }

    internal void OnModelStatus(ModelStatus status)
    {
        lock (_gate)
        {
            if (status.State == ModelInstallState.Downloading)
            {
                _downloads[status.Model.Id] = status;
            }
            else
            {
                _downloads.Remove(status.Model.Id);
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
                    _titles[id] = record.Title;
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

    /// <summary>Running first (recording, the pipeline, downloads), then what waits; each group in a stable order.</summary>
    private List<ActivityItem> Build()
    {
        var items = new List<ActivityItem>();
        if (_recorder.State is RecorderState.Recording or RecorderState.Paused)
        {
            items.Add(new ActivityItem("rec", ActivityKind.Recording, _recorder.Current?.Title ?? string.Empty, null, false));
        }

        var canIndex = _processor?.CanIndex == true;
        lock (_gate)
        {
            var sessions = _processing.Values.OrderBy(static s => s.Session.Value).ToArray();
            var queued = new List<ActivityItem>();
            foreach (var status in sessions)
            {
                var title = _titles.GetValueOrDefault(status.Session, string.Empty);
                var id = status.Session.ToString();
                switch (status.Stage)
                {
                    case SessionProcessingStage.Queued:
                        queued.Add(new ActivityItem("asr:" + id, ActivityKind.Transcribing, title, null, true));
                        break;
                    case SessionProcessingStage.Indexing:
                        items.Add(new ActivityItem("idx:" + id, ActivityKind.Indexing, title, null, false));
                        break;
                    default:
                        items.Add(new ActivityItem("asr:" + id, ActivityKind.Transcribing, title, Math.Clamp(status.Progress, 0, 1), false));

                        // The indexing that follows waits for the transcript.
                        if (canIndex)
                        {
                            queued.Add(new ActivityItem("idx:" + id, ActivityKind.Indexing, title, null, true));
                        }

                        break;
                }
            }

            foreach (var status in _downloads.Values.OrderBy(static s => s.Model.Id, StringComparer.Ordinal))
            {
                double? progress = status.BytesTotal is > 0 ? Math.Clamp((double)status.BytesDone / status.BytesTotal.Value, 0, 1) : null;
                items.Add(new ActivityItem("model:" + status.Model.Id, ActivityKind.DownloadingModel, status.Model.DisplayName, progress, false));
            }

            items.AddRange(queued);
        }

        return items;
    }
}
