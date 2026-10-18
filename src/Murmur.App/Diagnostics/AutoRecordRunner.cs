using System.Diagnostics;
using System.Text.Json;
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

            var startError = await StartRecordingAsync();
            if (startError is not null)
            {
                return report with { Error = startError };
            }

            // A track the recorder had to skip (e.g. the microphone denied by Windows privacy settings) is a warning:
            // the run goes on with the other track and still reports every number.
            var warnings = new List<string>();
            AddWarning(warnings, _recorder.Current?.Error);

            var machine = new List<double>(_options.Seconds);
            var oneCore = new List<double>(_options.Seconds);
            var workingSet = new List<double>(_options.Seconds);
            var privateMax = 0.0;

            sampler.Reset();
            var startedAt = Stopwatch.GetTimestamp();
            using (var timer = new PeriodicTimer(SampleInterval))
            {
                for (var i = 0; i < _options.Seconds; i++)
                {
                    await timer.WaitForNextTickAsync(cancellationToken);
                    var sample = sampler.Sample();
                    machine.Add(sample.CpuMachinePct);
                    oneCore.Add(sample.CpuCorePct);
                    workingSet.Add(sample.WorkingSetMb);
                    privateMax = Math.Max(privateMax, sample.PrivateMb);
                }
            }

            var duration = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
            await StopRecordingAsync();

            var session = stopped ?? throw new InvalidOperationException("The recording did not stop cleanly.");
            if (session.State != SessionState.Failed)
            {
                AddWarning(warnings, session.Error);
            }

            report = report with
            {
                Orb = _options.ShowOrb,
                DurationS = Math.Round(duration, 3),
                CpuAvgPct = Round(Average(machine)),
                CpuP95Pct = Round(ProcessSampler.Percentile(machine, 95)),
                CpuAvgPctOneCore = Round(Average(oneCore)),
                WsAvgMb = Round(Average(workingSet)),
                WsMaxMb = Round(Max(workingSet)),
                PrivateMaxMb = Round(privateMax),
                RecorderStats = ToReport(_recorder.Stats),
                Session = DescribeSession(session),
                Warnings = warnings.Count == 0 ? null : warnings,
            };

            return session.State == SessionState.Failed
                ? report with { Error = session.Error ?? "The session failed." }
                : report;
        }
        finally
        {
            _recording.Stopped -= OnStopped;
            if (_recorder.State == RecorderState.Recording)
            {
                await StopRecordingAsync();
            }
        }
    }

    private async Task<string?> StartRecordingAsync()
    {
        string? error = null;
        await _runOnUi(async () =>
        {
            // Mic only when one exists; the system loopback is always requested.
            _recording.Microphone = _recording.MicrophoneAvailable;
            _recording.SystemAudio = _recording.SystemAudioAvailable;
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
            if (!_recording.IsRecording)
            {
                error = _recording.Error ?? "The recording did not start.";
            }
        });

        return error;
    }

    private Task StopRecordingAsync() => _runOnUi(_recording.StopAsync);

    /// <summary>
    /// Adds a session warning once. The stop repeats the start's warning, possibly with a screen error in front: the
    /// fuller text then replaces the one it contains.
    /// </summary>
    private static void AddWarning(List<string> warnings, string? warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return;
        }

        foreach (var existing in warnings)
        {
            if (existing.Contains(warning, StringComparison.Ordinal))
            {
                return;
            }
        }

        warnings.RemoveAll(existing => warning.Contains(existing, StringComparison.Ordinal));
        warnings.Add(warning);
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

    private static double Round(double value) => Math.Round(value, 3);
}
