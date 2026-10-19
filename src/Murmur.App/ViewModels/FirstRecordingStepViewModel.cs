using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Controls;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.Core.Capture;
using Murmur.Core.Processing;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Settings;
using Murmur.Core.Transcription;

namespace Murmur.App.ViewModels;

public enum TrialState
{
    /// <summary>Nothing recorded yet (or the trial was left and cleaned up).</summary>
    Ready,
    Starting,

    /// <summary>Recording; the countdown runs.</summary>
    Recording,
    Stopping,

    /// <summary>The transcription model is not installed yet; the trial continues by itself when it is.</summary>
    WaitingForModel,
    Transcribing,

    /// <summary>The transcript is shown (or the trial finished without one).</summary>
    Done,
    Failed,
}

/// <summary>One line of the recognised text: who spoke, when (a chip in Geist Mono) and what.</summary>
public sealed record TrialSegment(string Speaker, string Initial, string Time, string Text);

/// <summary>
/// Step 5: a ten-second recording through the normal <see cref="IRecorder"/> (microphone and system audio, no screen), then
/// the transcript the normal post-recording pipeline produces. The recording is a test: the session is deleted from the library
/// when the user leaves the step, but never while the processor still works on it.
/// </summary>
/// <remarks>
/// <para>The trial starts by itself when the step opens, like the mockup's countdown. The orb follows the recorder's levels
/// (<see cref="ILevelSource"/>), the countdown comes from the injected clock. Without the Parakeet model the step waits for
/// the download of step 3 and queues the session once it is installed.</para>
/// <para><b>Deleting.</b> The processor takes the session over the moment the recorder stops. There is no way to cancel its
/// job, so the session is deleted only once the processor reports a final stage (completed, failed or "model missing" - all
/// of which end the job); if the user leaves earlier, the deletion waits for that event for as long as the app runs.</para>
/// </remarks>
public sealed partial class FirstRecordingStepViewModel : OnboardingStepViewModel, ILevelSource, IDisposable
{
    /// <summary>The length of the trial recording.</summary>
    public static readonly TimeSpan TrialLength = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IRecorder _recorder;
    private readonly ICapture _capture;
    private readonly ISessionStore _sessions;
    private readonly ISessionProcessing? _processing;
    private readonly ITranscriptStore? _transcripts;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly Func<AppSettings> _settings;
    private readonly ModelsStepViewModel _models;
    private readonly HashSet<SessionId> _deleteWhenFinished = [];
    private CancellationTokenSource? _runCts;
    private Task _run = Task.CompletedTask;
    private Task _leave = Task.CompletedTask;
    private SessionRecord? _trial;
    private bool _active;
    private bool _loadingTranscript;
    private bool _queuedAfterModel;

    internal FirstRecordingStepViewModel(
        IRecorder recorder,
        ICapture capture,
        ISessionStore sessions,
        ISessionProcessing? processing,
        ITranscriptStore? transcripts,
        TimeProvider time,
        Action<Action> post,
        Func<AppSettings> settings,
        ModelsStepViewModel models)
    {
        _recorder = recorder;
        _capture = capture;
        _sessions = sessions;
        _processing = processing;
        _transcripts = transcripts;
        _time = time;
        _post = post;
        _settings = settings;
        _models = models;
        ContinueLabel = Strings.Onb5_Open;
        ClockText = FormatClock(TrialLength);
        if (_processing is not null)
        {
            _processing.ProgressChanged += OnProcessingProgress;
        }

        models.PropertyChanged += OnModelsChanged;
    }

    public override string Title => Strings.Onb5_Headline;

    public override string Subtitle => Strings.Onb5_Subline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecording), nameof(StatusLabel), nameof(PlaceholderText), nameof(ShowPlaceholder), nameof(ShowProgress), nameof(ShowTranscript), nameof(ShowMessage), nameof(ShowRetry))]
    public partial TrialState State { get; set; }

    /// <summary>Remaining time, <c>00:10</c> down to <c>00:00</c>, in Geist Mono.</summary>
    [ObservableProperty]
    public partial string ClockText { get; set; }

    /// <summary>Transcription progress (or the model download while waiting for it), 0 to 100.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>The recognised lines.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTranscript), nameof(ShowMessage), nameof(MessageText))]
    public partial IReadOnlyList<TrialSegment> Segments { get; set; } = [];

    /// <summary>Why the trial failed, or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMessage), nameof(MessageText))]
    public partial string? Error { get; set; }

    /// <summary>The orb moves with the audio.</summary>
    public bool IsRecording => State == TrialState.Recording;

    /// <summary>The word in the countdown pill.</summary>
    public string StatusLabel => State switch
    {
        TrialState.Starting => Strings.Onb5_Starting,
        TrialState.Recording => Strings.Onb5_Listening,
        TrialState.Stopping or TrialState.Transcribing or TrialState.WaitingForModel => Strings.Onb5_Transcribing,
        TrialState.Done => Strings.Onb5_Done,
        _ => string.Empty,
    };

    /// <summary>The dashed card in place of the transcript.</summary>
    public string PlaceholderText => State switch
    {
        TrialState.WaitingForModel => _models.IsRequiredFailed ? Strings.Onb5_ModelFailed : Strings.Onb5_WaitingModel,
        TrialState.Transcribing or TrialState.Stopping => Strings.Onb5_Transcribing,
        _ => Strings.Onb5_Placeholder,
    };

    public bool ShowPlaceholder => State is TrialState.Ready or TrialState.Starting or TrialState.Recording or TrialState.Stopping or TrialState.WaitingForModel or TrialState.Transcribing;

    /// <summary>A progress bar under the placeholder: the model download or the transcription.</summary>
    public bool ShowProgress => State is TrialState.WaitingForModel or TrialState.Transcribing;

    public bool ShowTranscript => State == TrialState.Done && Segments.Count > 0;

    /// <summary>A finished trial with nothing recognised, or a failed one.</summary>
    public bool ShowMessage => State == TrialState.Failed || (State == TrialState.Done && Segments.Count == 0);

    public string MessageText => Error ?? Strings.Onb5_NothingRecognised;

    public bool ShowRetry => State is TrialState.Done or TrialState.Failed;

    /// <inheritdoc />
    float ILevelSource.Level => Level;

    /// <summary>Peak level of the recorder, the louder of the microphone and the system audio.</summary>
    public float Level
    {
        get
        {
            var levels = _recorder.Levels;
            return Math.Max(levels.Microphone, levels.SystemAudio);
        }
    }

    /// <summary>The session of the trial that is not deleted yet (for tests).</summary>
    internal SessionRecord? Trial => _trial;

    /// <summary>The last deletion of a trial session (for tests).</summary>
    internal Task DeleteTask { get; private set; } = Task.CompletedTask;

    /// <summary>The trial run (record, wait, transcript); completes when it ended or was cancelled (for tests).</summary>
    internal Task Run => _run;

    internal override Task Settled => _leave;

    /// <summary>A trial session was deleted from the library.</summary>
    internal event Action? TrialSessionDeleted;

    internal override void Activate()
    {
        _active = true;
        if (!_leave.IsCompleted)
        {
            _ = StartWhenLeftAsync();
        }
        else if (State == TrialState.Ready)
        {
            _run = RunAsync();
        }
    }

    /// <summary>The step was shown again before the clean-up of the last visit finished: the new trial waits for it.</summary>
    private async Task StartWhenLeftAsync()
    {
        await _leave.ConfigureAwait(true);
        if (_active && State == TrialState.Ready)
        {
            _run = RunAsync();
        }
    }

    internal override void Deactivate()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        _leave = LeaveAsync();
    }

    [RelayCommand]
    private async Task TryAgainAsync()
    {
        if (!_active)
        {
            return;
        }

        await LeaveAsync().ConfigureAwait(true);
        if (_active && State == TrialState.Ready)
        {
            _run = RunAsync();
        }
    }

    public void Dispose()
    {
        _models.PropertyChanged -= OnModelsChanged;
        if (_processing is not null)
        {
            _processing.ProgressChanged -= OnProcessingProgress;
        }
    }

    // The trial -------------------------------------------------------------------------------------------------------

    private async Task RunAsync()
    {
        var cts = _runCts = new CancellationTokenSource();
        try
        {
            await RunCoreAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Left the step: LeaveAsync stops the recording and cleans up.
        }
        catch (Exception ex)
        {
            Fail(UiText.Format(Strings.Onb5_RecordFailed, ex.Message));
        }
    }

    private async Task RunCoreAsync(CancellationToken token)
    {
        Segments = [];
        Error = null;
        Progress = 0;
        _loadingTranscript = false;
        _queuedAfterModel = false;
        ClockText = FormatClock(TrialLength);
        State = TrialState.Starting;

        var capabilities = _capture.Capabilities;
        if (!capabilities.Microphone && !capabilities.SystemAudio)
        {
            Fail(Strings.Onb5_NoAudio);
            return;
        }

        if (_recorder.State != RecorderState.Idle)
        {
            Fail(Strings.Onb5_Busy);
            return;
        }

        var saved = _settings();
        var options = new RecordingOptions(
            Microphone: capabilities.Microphone,
            SystemAudio: capabilities.SystemAudio,
            Screen: false,
            MicrophoneDeviceId: saved.MicrophoneDeviceId,
            SystemDeviceId: saved.SystemAudioDeviceId,
            Title: TrialSessionCleaner.TitleOfTrial());

        try
        {
            _trial = await _recorder.StartAsync(options, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Fail(UiText.Format(Strings.Onb5_RecordFailed, ex.Message));
            return;
        }

        token.ThrowIfCancellationRequested();
        State = TrialState.Recording;
        var started = _time.GetTimestamp();
        while (true)
        {
            await Task.Delay(PollInterval, _time, token).ConfigureAwait(true);
            var elapsed = _time.GetElapsedTime(started);
            if (_recorder.State != RecorderState.Recording)
            {
                // Every source failed or the window is closing: the recorder ended the session by itself.
                Fail(Strings.Onb5_StoppedItself);
                return;
            }

            ClockText = FormatClock(TrialLength - elapsed);
            if (elapsed >= TrialLength)
            {
                break;
            }
        }

        State = TrialState.Stopping;
        try
        {
            _trial = await _recorder.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Fail(UiText.Format(Strings.Onb5_RecordFailed, ex.Message));
            return;
        }

        await BeginProcessingAsync(_trial).ConfigureAwait(true);
    }

    private async Task BeginProcessingAsync(SessionRecord finished)
    {
        if (finished.State != SessionState.Recorded)
        {
            // The recorder could not keep any track: nothing to transcribe, and the pipeline will not touch the session.
            Fail(UiText.Format(Strings.Onb5_RecordFailed, finished.Error ?? finished.State.ToString()));
            return;
        }

        if (_processing is null || _transcripts is null)
        {
            State = TrialState.Done;
            Error = Strings.Onb5_NoTranscription;
            return;
        }

        if (!_processing.IsAsrInstalled)
        {
            EnterWaitingForModel();
            return;
        }

        await EvaluateAsync(_processing.GetStatus(finished.Id)).ConfigureAwait(true);
    }

    /// <summary>Reacts to the pipeline's status of the trial session (null = the job is not reported yet, so it is queued).</summary>
    private async Task EvaluateAsync(SessionProcessingStatus? status)
    {
        switch (status?.Stage)
        {
            case SessionProcessingStage.ModelMissing:
                EnterWaitingForModel();
                break;
            case SessionProcessingStage.Failed:
                Fail(UiText.Format(Strings.Onb5_TranscribeFailed, status!.Message ?? string.Empty));
                break;
            case SessionProcessingStage.Indexing:
            case SessionProcessingStage.Completed:
                await ShowTranscriptAsync().ConfigureAwait(true);
                break;
            default:
                State = TrialState.Transcribing;
                Progress = (status?.Progress ?? 0) * 100;
                break;
        }
    }

    private void EnterWaitingForModel()
    {
        State = TrialState.WaitingForModel;
        Progress = _models.RequiredPercent;
        OnPropertyChanged(nameof(PlaceholderText));
        if (_models.RequiredReady)
        {
            _ = QueueAfterModelAsync();
        }
    }

    /// <summary>The model is there: the session that waited is queued for the normal pipeline.</summary>
    private async Task QueueAfterModelAsync()
    {
        if (_queuedAfterModel || _trial is not { } trial || _processing is null)
        {
            return;
        }

        _queuedAfterModel = true;
        State = TrialState.Transcribing;
        Progress = 0;
        try
        {
            await _processing.EnqueueAsync(trial.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Fail(UiText.Format(Strings.Onb5_TranscribeFailed, ex.Message));
        }
    }

    private async Task ShowTranscriptAsync()
    {
        if (_loadingTranscript || _trial is not { } trial || _transcripts is null)
        {
            return;
        }

        _loadingTranscript = true;
        try
        {
            var transcript = await _transcripts.GetTranscriptAsync(trial.Id, CancellationToken.None).ConfigureAwait(true);
            if (_trial?.Id != trial.Id)
            {
                // The step was left (or restarted) while the transcript was read.
                return;
            }

            var lines = new List<TrialSegment>();
            if (transcript is not null)
            {
                foreach (var segment in transcript.Segments)
                {
                    var text = segment.Text.Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    var speaker = segment.Speaker == Speaker.Me ? Strings.Segment_Me : Strings.Segment_Others;
                    lines.Add(new TrialSegment(speaker, speaker[..1], UiFormat.Clock(segment.Start), text));
                }
            }

            Segments = lines;
            Error = null;
            State = TrialState.Done;
        }
        catch (Exception ex)
        {
            Fail(UiText.Format(Strings.Onb5_TranscribeFailed, ex.Message));
        }
    }

    private void Fail(string message)
    {
        Error = message;
        State = TrialState.Failed;
    }

    // Leaving ---------------------------------------------------------------------------------------------------------

    /// <summary>Stops a running trial, then deletes (or schedules the deletion of) its session and returns to <see cref="TrialState.Ready"/>.</summary>
    private async Task LeaveAsync()
    {
        _runCts?.Cancel();
        try
        {
            await _run.ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The run reported its own failure; only the clean-up matters now.
        }

        _runCts?.Dispose();
        _runCts = null;

        if (_recorder.State == RecorderState.Recording && _trial is not null)
        {
            try
            {
                _trial = await _recorder.StopAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // The recorder stopped by itself in the meantime.
            }
        }

        var trial = _trial;
        _trial = null;
        State = TrialState.Ready;
        Segments = [];
        Error = null;
        if (trial is not null)
        {
            // Our copy of the record can be stale (the recorder ended the session by itself): what the store says decides.
            try
            {
                trial = await _sessions.GetSessionAsync(trial.Id, CancellationToken.None).ConfigureAwait(true) ?? trial;
            }
            catch (Exception)
            {
                // Keep the copy we have.
            }

            await DeleteWhenSafeAsync(trial).ConfigureAwait(true);
        }
    }

    /// <summary>Deletes the trial session now when the pipeline is done with it, else as soon as it reports that it is.</summary>
    private Task DeleteWhenSafeAsync(SessionRecord trial)
    {
        if (IsFinished(trial))
        {
            return DeleteTask = DeleteAsync(trial.Id);
        }

        _deleteWhenFinished.Add(trial.Id);
        return Task.CompletedTask;
    }

    private bool IsFinished(SessionRecord trial)
    {
        if (_processing is null)
        {
            return true;
        }

        return _processing.GetStatus(trial.Id) switch
        {
            { Stage: SessionProcessingStage.Completed or SessionProcessingStage.Failed or SessionProcessingStage.ModelMissing } => true,
            not null => false,

            // Never reported: the pipeline takes up a Recorded session (and one the recorder ended by itself) as soon as
            // the recorder is idle, so only a session that is in none of those states was never handed to it.
            null => trial.State is not (SessionState.Recorded or SessionState.Recording),
        };
    }

    private async Task DeleteAsync(SessionId id)
    {
        try
        {
            await _sessions.DeleteSessionAsync(id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Best effort: a session that stays is an ordinary recording in the library.
            return;
        }

        TrialSessionDeleted?.Invoke();
    }

    // Events ----------------------------------------------------------------------------------------------------------

    private void OnProcessingProgress(SessionProcessingStatus status) => _post(() => HandleProgress(status));

    private void HandleProgress(SessionProcessingStatus status)
    {
        if (_deleteWhenFinished.Contains(status.Session)
            && status.Stage is SessionProcessingStage.Completed or SessionProcessingStage.Failed or SessionProcessingStage.ModelMissing)
        {
            _deleteWhenFinished.Remove(status.Session);
            DeleteTask = DeleteAsync(status.Session);
            return;
        }

        if (_trial is { } trial && trial.Id == status.Session && State is TrialState.WaitingForModel or TrialState.Transcribing)
        {
            _ = EvaluateAsync(status);
        }
    }

    private void OnModelsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (State != TrialState.WaitingForModel)
        {
            return;
        }

        if (e.PropertyName == nameof(ModelsStepViewModel.RequiredPercent))
        {
            Progress = _models.RequiredPercent;
        }
        else if (e.PropertyName == nameof(ModelsStepViewModel.RequiredState))
        {
            OnPropertyChanged(nameof(PlaceholderText));
            if (_models.RequiredReady || _processing?.IsAsrInstalled == true)
            {
                _ = QueueAfterModelAsync();
            }
        }
    }

    private static string FormatClock(TimeSpan remaining)
    {
        var seconds = (int)Math.Clamp(Math.Ceiling(remaining.TotalSeconds), 0, 99);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"00:{seconds:00}");
    }
}
