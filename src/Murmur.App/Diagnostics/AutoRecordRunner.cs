using System.Diagnostics;
using System.Text.Json;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.ViewModels;
using Murmur.Core.Audio;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.App.Diagnostics;

/// <summary>
/// CI perf mode (<c>--autorecord</c>). With the Recording screen shown it measures the idle CPU, then records through the
/// real view model (so the orb and the timer run as they do for a user), samples the process once per second off the UI
/// thread and writes a <see cref="PerfReport"/>.
/// </summary>
public sealed class AutoRecordRunner
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>A <see cref="PerfMemorySample"/> is kept every this many one-second samples (and for the last one).</summary>
    public const int MemorySampleEvery = 10;

    private const double BytesPerMb = 1024.0 * 1024.0;

    private readonly AutoRecordOptions _options;
    private readonly RecordingViewModel _recording;
    private readonly IRecorder _recorder;
    private readonly ISessionStore _store;
    private readonly Func<Func<Task>, Task> _runOnUi;

    /// <param name="runOnUi">Runs an async action on the UI thread and completes when it has finished.</param>
    public AutoRecordRunner(
        AutoRecordOptions options,
        RecordingViewModel recording,
        IRecorder recorder,
        ISessionStore store,
        Func<Func<Task>, Task> runOnUi)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runOnUi);
        _options = options;
        _recording = recording;
        _recorder = recorder;
        _store = store;
        _runOnUi = runOnUi;
    }

    /// <returns>The process exit code: 0 on success, 1 on failure (the report then carries an <c>error</c>).</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var report = new PerfReport();
        try
        {
            report = await MeasureAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            report = report with { Error = ex.Message };
        }

        try
        {
            Write(report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot write {_options.PerfOut}: {ex.Message}");
            return 1;
        }

        return report.Error is null ? 0 : 1;
    }

    private async Task<PerfReport> MeasureAsync(CancellationToken cancellationToken)
    {
        SessionRecord? stopped = null;
        void OnStopped(SessionRecord session) => stopped = session;

        // Screen problems that did not stop the recording, in the order they were raised (any thread).
        var faultWarnings = new List<string>();
        void OnWarning(RecorderWarning warning)
        {
            lock (faultWarnings)
            {
                // The report is read by people and scripts, not by the user of the UI: always English.
                var text = SessionNoteText.Describe(warning, UiCulture.English);
                if (!faultWarnings.Contains(text))
                {
                    faultWarnings.Add(text);
                }
            }
        }

        using var sampler = new ProcessSampler();
        var report = new PerfReport();
        _recording.Stopped += OnStopped;
        try
        {
            if (_options.IdleSeconds > 0)
            {
                // Let window creation and the first frames settle, then measure the idle window.
                await Task.Delay(SettleTime, cancellationToken);
                sampler.Reset();
                await Task.Delay(TimeSpan.FromSeconds(_options.IdleSeconds), cancellationToken);
                report = report with { IdleCpuPct = sampler.Sample().CpuMachinePct };
            }

            _recorder.Warning += OnWarning;
            var started = await StartRecordingAsync();
            if (started.Error is not null)
            {
                return report with { Error = started.Error };
            }

            // A track the recorder had to skip (e.g. the microphone denied by Windows privacy settings) is a warning:
            // the run goes on with the other track and still reports every number.
            var warnings = new List<string>();
            AddWarning(warnings, _recorder.Current?.Error);

            var machine = new List<double>(_options.Seconds);
            var oneCore = new List<double>(_options.Seconds);
            var workingSet = new List<double>(_options.Seconds);
            var privateBytes = new List<double>(_options.Seconds);
            var memory = new List<PerfMemorySample>((_options.Seconds / MemorySampleEvery) + 1);
            var privateMax = 0.0;
            var gcHeapMax = 0.0;

            var uiAllocatedAtStart = await UiThreadAllocatedBytesAsync();
            long samplerAllocated = 0;
            sampler.Reset();
            var startedAt = Stopwatch.GetTimestamp();
            var allocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            var gen0AtStart = GC.CollectionCount(0);
            var gen2AtStart = GC.CollectionCount(2);
            using (var timer = new PeriodicTimer(SampleInterval))
            {
                for (var i = 0; i < _options.Seconds; i++)
                {
                    await timer.WaitForNextTickAsync(cancellationToken);
                    var samplerBefore = GC.GetAllocatedBytesForCurrentThread();
                    var sample = sampler.Sample();
                    machine.Add(sample.CpuMachinePct);
                    oneCore.Add(sample.CpuCorePct);
                    workingSet.Add(sample.WorkingSetMb);
                    privateBytes.Add(sample.PrivateMb);
                    privateMax = Math.Max(privateMax, sample.PrivateMb);
                    var gcHeap = GC.GetTotalMemory(forceFullCollection: false) / BytesPerMb;
                    gcHeapMax = Math.Max(gcHeapMax, gcHeap);
                    if (i % MemorySampleEvery == 0 || i == _options.Seconds - 1)
                    {
                        memory.Add(new PerfMemorySample
                        {
                            Seconds = Round(Stopwatch.GetElapsedTime(startedAt).TotalSeconds),
                            WsMb = Round(sample.WorkingSetMb),
                            PrivateMb = Round(sample.PrivateMb),
                            GcHeapMb = Round(gcHeap),
                            GcCommittedMb = Round(GC.GetGCMemoryInfo().TotalCommittedBytes / BytesPerMb),
                            GcAllocatedMb = Round((GC.GetTotalAllocatedBytes(precise: false) - allocatedAtStart) / BytesPerMb),
                            Gen0Collections = GC.CollectionCount(0) - gen0AtStart,
                            Gen2Collections = GC.CollectionCount(2) - gen2AtStart,
                        });
                    }

                    samplerAllocated += GC.GetAllocatedBytesForCurrentThread() - samplerBefore;
                }
            }

            var allocated = (GC.GetTotalAllocatedBytes(precise: false) - allocatedAtStart) / BytesPerMb;
            var uiAllocated = (await UiThreadAllocatedBytesAsync() - uiAllocatedAtStart) / BytesPerMb;

            // The length of the session timeline the files are on (the audio starts at session time 0), read as the stop
            // is requested. Not the length of the sampling window above: that starts after StartAsync returns, which
            // with the screen on includes the screen's start-up while the audio is already recording. A recorder that
            // already ended the session by itself (every audio source failed) reports no elapsed time any more: the
            // session's own duration stands in then.
            var duration = TimeSpan.Zero;
            await _runOnUi(async () =>
            {
                duration = _recorder.Elapsed;
                await _recording.StopAsync();
            });

            var session = stopped ?? throw new InvalidOperationException("The recording did not stop cleanly.");

            // The start-up warning, then the screen problems in the order they were raised; whatever else the stop
            // reported (e.g. a screen segment that could not be finalized) comes last.
            lock (faultWarnings)
            {
                foreach (var problem in faultWarnings)
                {
                    AddWarning(warnings, problem);
                }
            }

            if (session.State != SessionState.Failed)
            {
                AddWarning(warnings, session.Error);
            }

            report = report with
            {
                Orb = _options.ShowOrb,
                DurationS = Math.Round((duration > TimeSpan.Zero ? duration : session.Duration).TotalSeconds, 3),
                ScreenStartMs = ScreenStartMs(session, started.Elapsed),
                CpuAvgPct = Round(Average(machine)),
                CpuP95Pct = Round(ProcessSampler.Percentile(machine, 95)),
                CpuAvgPctOneCore = Round(Average(oneCore)),
                WsAvgMb = Round(Average(workingSet)),
                WsMaxMb = Round(Max(workingSet)),
                PrivateMaxMb = Round(privateMax),
                WsSlopeMbPerMin = Round(SlopePerMinute(workingSet)),
                PrivateSlopeMbPerMin = Round(SlopePerMinute(privateBytes)),
                GcHeapMaxMb = Round(gcHeapMax),
                GcAllocatedMb = Round(allocated),
                UiThreadAllocatedMb = Round(uiAllocated),
                SamplerAllocatedMb = Round(samplerAllocated / BytesPerMb),
                Memory = memory,
                RecorderStats = ToReport(_recorder.Stats),
                Session = DescribeSession(session),
                Warnings = warnings.Count == 0 ? null : warnings,
            };

            return session.State == SessionState.Failed
                ? report with { Error = SessionNoteText.Describe(session.Error, UiCulture.English) ?? "The session failed." }
                : report;
        }
        finally
        {
            _recording.Stopped -= OnStopped;
            _recorder.Warning -= OnWarning;
            if (_recorder.State == RecorderState.Recording)
            {
                await StopRecordingAsync();
            }
        }
    }

    /// <summary>Outcome of starting the recording: the error (if any) and the session time when the start call returned.</summary>
    private readonly record struct StartResult(string? Error, TimeSpan Elapsed);

    private async Task<StartResult> StartRecordingAsync()
    {
        string? error = null;
        var elapsed = TimeSpan.Zero;
        await _runOnUi(async () =>
        {
            // Mic only when one exists (and not left out with --no-mic); the system loopback unless --no-system.
            _recording.Microphone = _options.Microphone && _recording.MicrophoneAvailable;
            _recording.SystemAudio = _options.SystemAudio && _recording.SystemAudioAvailable;
            if (_options.Screen && !_recording.ScreenAvailable)
            {
                error = "Screen capture was requested but is not available on this platform.";
                return;
            }

            _recording.ShowOrb = _options.ShowOrb;
            _recording.Screen = _options.Screen;
            if (!_recording.Microphone && !_recording.SystemAudio)
            {
                error = "No audio source is available.";
                return;
            }

            await _recording.StartStopAsync();
            elapsed = _recorder.Elapsed;
            if (!_recording.IsRecording)
            {
                error = _recording.Error ?? "The recording did not start.";
            }
        });

        return new StartResult(error, elapsed);
    }

    /// <summary>
    /// How long starting the screen took: the session time at which the start call returned minus the session time at
    /// which the recorder called <see cref="Murmur.Core.Capture.IScreenRecorder.StartSegmentAsync"/> for the first
    /// segment (its <see cref="ScreenSegment.Offset"/>). That is the screen recorder's start-up (device, encoder,
    /// capture session) plus the recorder's own bookkeeping after it, without the audio start that precedes it.
    /// <c>null</c> when the session has no screen segment.
    /// </summary>
    internal static double? ScreenStartMs(SessionRecord session, TimeSpan startReturnedAt)
    {
        if (session.ScreenSegments.Count == 0)
        {
            return null;
        }

        return Round(Math.Max(0, (startReturnedAt - session.ScreenSegments[0].Offset).TotalMilliseconds));
    }

    private Task StopRecordingAsync() => _runOnUi(_recording.StopAsync);

    /// <summary>Bytes the UI thread has allocated so far (read on the UI thread).</summary>
    private async Task<long> UiThreadAllocatedBytesAsync()
    {
        long bytes = 0;
        await _runOnUi(() =>
        {
            bytes = GC.GetAllocatedBytesForCurrentThread();
            return Task.CompletedTask;
        });
        return bytes;
    }

    /// <summary>
    /// Adds the problems a stored text (<c>SessionRecord.Error</c>: one line per code or technical message) carries, in
    /// English and unless the list already says them.
    /// </summary>
    private static void AddWarning(List<string> warnings, string? stored)
    {
        foreach (var note in SessionNotes.Parse(stored))
        {
            var text = SessionNoteText.DescribeNote(note, UiCulture.English);
            if (!warnings.Contains(text))
            {
                warnings.Add(text);
            }
        }
    }

    private PerfSession DescribeSession(SessionRecord session)
    {
        var files = _store.GetFiles(session.Id);
        return new PerfSession
        {
            Id = session.Id.ToString(),
            Folder = files.Directory,
            MicSeconds = Round(WavSeconds(files.MicrophoneAudio)),
            SystemSeconds = Round(WavSeconds(files.SystemAudio)),
            ScreenSegments = session.ScreenSegments.Count,
            ScreenSegmentTimes = [.. session.ScreenSegments.Select(s => new PerfScreenSegment
            {
                Index = s.Index,
                OffsetS = Round(s.Offset.TotalSeconds),
                DurationS = Round(s.Duration.TotalSeconds),
            })],
        };
    }

    private static double WavSeconds(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            using var reader = WavReader.Open(path);
            return reader.Duration.TotalSeconds;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return 0;
        }
    }

    private static PerfRecorderStats ToReport(RecorderStats stats) => new()
    {
        MicrophoneFrames = stats.MicrophoneFrames,
        SystemFrames = stats.SystemFrames,
        DroppedFrames = stats.DroppedFrames,
        ScreenFramesDropped = stats.ScreenFramesDropped,
        ScreenFaults = stats.ScreenFaults,
    };

    private void Write(PerfReport report)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_options.PerfOut));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_options.PerfOut, JsonSerializer.Serialize(report, PerfJsonContext.Default.PerfReport));
    }

    private static double Average(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sum = 0.0;
        foreach (var value in values)
        {
            sum += value;
        }

        return sum / values.Count;
    }

    private static double Max(List<double> values)
    {
        var max = 0.0;
        foreach (var value in values)
        {
            max = Math.Max(max, value);
        }

        return max;
    }

    /// <summary>
    /// Least-squares slope of <paramref name="perSecond"/> (one value per second) in units per minute; 0 for fewer
    /// than two values.
    /// </summary>
    internal static double SlopePerMinute(IReadOnlyList<double> perSecond)
    {
        ArgumentNullException.ThrowIfNull(perSecond);
        var n = perSecond.Count;
        if (n < 2)
        {
            return 0;
        }

        var meanX = (n - 1) / 2.0;
        var meanY = 0.0;
        for (var i = 0; i < n; i++)
        {
            meanY += perSecond[i];
        }

        meanY /= n;
        double covariance = 0, variance = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = i - meanX;
            covariance += dx * (perSecond[i] - meanY);
            variance += dx * dx;
        }

        return covariance / variance * 60.0;
    }

    private static double Round(double value) => Math.Round(value, 3);
}
