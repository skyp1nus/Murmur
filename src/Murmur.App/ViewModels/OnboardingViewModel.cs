using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.Core.Capture;
using Murmur.Core.Models;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Settings;

namespace Murmur.App.ViewModels;

/// <summary>One of the five progress segments at the top: filled for the finished steps and the current one.</summary>
public sealed record OnboardingSegment(bool IsFilled);

/// <summary>
/// The first-run onboarding: five steps (welcome, audio, models, preferences, first recording) in a shared frame with a step
/// indicator and Back / Skip / Continue. The shell shows it instead of the library while
/// <see cref="AppSettings.OnboardingCompleted"/> is false; finishing or skipping saves the flag at once and raises
/// <see cref="Completed"/>.
/// </summary>
/// <remarks>
/// <para>The steps reuse the Settings' view models (devices, folder, hotkey, models) and its settings writer, so every
/// choice is saved as it is made and the Settings screen shows the same state.</para>
/// <para>The shell builds it lazily and watches the flag ("Пройти знайомство ще раз" in the Settings clears it).</para>
/// </remarks>
public sealed partial class OnboardingViewModel : ObservableObject, IDisposable
{
    public const int StepCount = 5;

    private readonly SettingsWriter _writer;
    private readonly ISettingsStore _store;
    private readonly Action<Action> _post;
    private readonly OnboardingStepViewModel[] _steps;
    private bool _finishing;
    private bool _disposed;

    /// <param name="settings">The Settings screen's view model: devices, folder, hotkeys, models and the shared settings writer.</param>
    /// <param name="store">Where the completed flag is read and watched.</param>
    /// <param name="models">The model manager (recommended chat model, free space); <c>null</c> = no model downloads.</param>
    /// <param name="processing">The post-recording pipeline; <c>null</c> = the first recording shows no transcript.</param>
    /// <param name="transcripts">Reads the transcript of the trial recording.</param>
    /// <param name="time">The clock of the trial's countdown.</param>
    /// <param name="launcher">Opens the Windows microphone privacy page; the OS shell by default.</param>
    /// <param name="post">Marshals events from other threads to the UI thread; the UI dispatcher by default.</param>
    /// <param name="freeSpace">Free bytes of the drive of a folder; the drive query by default.</param>
    public OnboardingViewModel(
        SettingsViewModel settings,
        ISettingsStore store,
        ICapture capture,
        IRecorder recorder,
        ISessionStore sessions,
        IModelManager? models = null,
        ISessionProcessing? processing = null,
        ITranscriptStore? transcripts = null,
        TimeProvider? time = null,
        IPrivacySettingsLauncher? launcher = null,
        Action<Action>? post = null,
        Func<string?, long?>? freeSpace = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(sessions);
        _writer = settings.Writer;
        _store = store;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));

        var modelsStep = new ModelsStepViewModel(settings.Models, models);
        var firstRecording = new FirstRecordingStepViewModel(
            recorder,
            capture,
            sessions,
            processing,
            transcripts,
            time ?? TimeProvider.System,
            _post,
            () => store.Current,
            modelsStep);
        firstRecording.TrialSessionDeleted += () => TrialSessionDeleted?.Invoke();
        Audio = new AudioStepViewModel(settings.Devices, capture, launcher ?? SystemPrivacySettingsLauncher.Instance, _post);
        Models = modelsStep;
        Preferences = new PreferencesStepViewModel(settings, freeSpace);
        FirstRecording = firstRecording;
        _steps = [new WelcomeStepViewModel(), Audio, modelsStep, Preferences, firstRecording];
        CurrentStep = _steps[0];
        Segments = BuildSegments(1);
    }

    public AudioStepViewModel Audio { get; }

    public ModelsStepViewModel Models { get; }

    public PreferencesStepViewModel Preferences { get; }

    public FirstRecordingStepViewModel FirstRecording { get; }

    /// <summary>The current step, 1 to <see cref="StepCount"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepLabel), nameof(StepAutomationName))]
    public partial int StepNumber { get; set; } = 1;

    [ObservableProperty]
    public partial OnboardingStepViewModel CurrentStep { get; set; }

    /// <summary><c>1 / 5</c>, in Geist Mono.</summary>
    public string StepLabel => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{StepNumber} / {StepCount}");

    public string StepAutomationName => UiText.Format(Strings.Onb_StepAutomation, StepNumber, StepCount);

    [ObservableProperty]
    public partial IReadOnlyList<OnboardingSegment> Segments { get; set; }

    /// <summary>The onboarding was finished or skipped; the flag is saved. The shell shows the library.</summary>
    public event Action? Completed;

    /// <summary>A trial session was deleted from the library: a visible library list should reload.</summary>
    public event Action? TrialSessionDeleted;

    /// <summary>The onboarding has been completed (or skipped) before.</summary>
    public bool IsCompleted => _store.Current.OnboardingCompleted;

    /// <summary>The last finish (for tests); completes when the flag is saved and the trial is cleaned up.</summary>
    internal Task FinishTask { get; private set; } = Task.CompletedTask;

    /// <summary>Starts from the first step. Called when the onboarding becomes the visible page.</summary>
    public void Begin()
    {
        _finishing = false;
        foreach (var step in _steps)
        {
            step.Deactivate();
        }

        GoTo(1);
    }

    /// <summary>The window is closing or another page replaces the onboarding: everything the current step runs stops.</summary>
    public void Suspend() => CurrentStep.Deactivate();

    [RelayCommand]
    private void Continue()
    {
        if (_finishing || !CurrentStep.CanContinue)
        {
            return;
        }

        if (StepNumber >= StepCount)
        {
            Finish();
        }
        else
        {
            GoTo(StepNumber + 1);
        }
    }

    [RelayCommand]
    private void Secondary()
    {
        if (!_finishing && CurrentStep.ShowSecondary && CurrentStep.OnSecondary() && StepNumber < StepCount)
        {
            GoTo(StepNumber + 1);
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (!_finishing && StepNumber > 1)
        {
            GoTo(StepNumber - 1);
        }
    }

    [RelayCommand]
    private void Skip()
    {
        if (!_finishing && CurrentStep.ShowSkip)
        {
            Finish();
        }
    }

    /// <summary>Marks the onboarding completed without showing it (an older settings file of a user who already has recordings).</summary>
    internal Task CompleteSilentlyAsync() => _writer.UpdateAsync(s => s with { OnboardingCompleted = true });

    private void GoTo(int number)
    {
        CurrentStep.Deactivate();
        StepNumber = number;
        CurrentStep = _steps[number - 1];
        Segments = BuildSegments(number);
        CurrentStep.Activate();
    }

    private void Finish()
    {
        _finishing = true;
        FinishTask = FinishAsync();
    }

    private async Task FinishAsync()
    {
        var step = CurrentStep;
        step.Deactivate();

        // The flag first: whatever happens to the clean-up, the onboarding does not come back at the next start.
        await _writer.UpdateAsync(s => s with { OnboardingCompleted = true }).ConfigureAwait(true);
        await step.Settled.ConfigureAwait(true);
        Completed?.Invoke();
    }

    private static OnboardingSegment[] BuildSegments(int step)
    {
        var segments = new OnboardingSegment[StepCount];
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = new OnboardingSegment(i < step);
        }

        return segments;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Suspend();
        FirstRecording.Dispose();
    }
}
