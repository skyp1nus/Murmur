using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.Core.Capture;

namespace Murmur.App.ViewModels;

/// <summary>
/// Step 2: the microphone and the system-audio output, each with a device picker and a live level meter while the step is
/// shown. The pickers are the ones of the Settings (<see cref="DevicesSettingsViewModel"/>), so a choice is saved at once.
/// </summary>
/// <remarks>
/// Windows has no permission prompt for desktop apps: when opening the microphone is refused (<see cref="UnauthorizedAccessException"/>,
/// see D-023) the step explains it and opens the privacy page; "Try again" opens the source once more. Without a working
/// microphone the step offers "Continue without microphone" (recording without it is allowed).
/// </remarks>
public sealed partial class AudioStepViewModel : OnboardingStepViewModel
{
    private readonly DevicesSettingsViewModel _devices;
    private readonly IPrivacySettingsLauncher _launcher;
    private bool _active;

    internal AudioStepViewModel(DevicesSettingsViewModel devices, ICapture capture, IPrivacySettingsLauncher launcher, Action<Action> post)
    {
        _devices = devices;
        _launcher = launcher;
        Microphone = new AudioChannelViewModel(capture, AudioDeviceKind.Microphone, () => devices.SelectedMicrophone?.Id, post);
        SystemAudio = new AudioChannelViewModel(capture, AudioDeviceKind.SystemOutput, () => devices.SelectedSystemDevice?.Id, post);
        Microphone.PropertyChanged += OnMicrophoneChanged;
        SystemAudio.PropertyChanged += OnSystemAudioChanged;
        devices.PropertyChanged += OnDevicesChanged;
        ShowSkip = false;
        UpdateFooter();
    }

    public override string Title => Strings.Onb2_Headline;

    public override string Subtitle => Strings.Onb2_Subline;

    public DevicesSettingsViewModel Devices => _devices;

    public AudioChannelViewModel Microphone { get; }

    public AudioChannelViewModel SystemAudio { get; }

    /// <summary>The microphone works: the "allowed" card is shown under the pickers.</summary>
    public bool ShowGranted => Microphone.State == AudioChannelState.Live;

    /// <summary>Windows refuses the microphone: the permission card with the button that opens the privacy page.</summary>
    public bool ShowDenied => Microphone.State == AudioChannelState.Denied;

    public bool ShowNoMicrophone => Microphone.State == AudioChannelState.NoDevice;

    public bool ShowMicrophoneFailed => Microphone.State == AudioChannelState.Failed;

    /// <summary>The permission button opens a page that exists on this platform.</summary>
    public bool CanOpenSettings => _launcher.CanOpenMicrophoneSettings;

    /// <summary>The microphone failed with something other than a refusal: its reason, shown under the headline of the card.</summary>
    public string MicrophoneFailedText => UiText.Format(Strings.Onb2_MicFailedText, Microphone.Error ?? string.Empty);

    public bool ShowSystemUnavailable => SystemAudio.State is AudioChannelState.NoDevice or AudioChannelState.Failed or AudioChannelState.Denied;

    /// <summary>Why the system audio is not metered; the recording continues with the microphone alone.</summary>
    public string SystemUnavailableText => SystemAudio.State == AudioChannelState.NoDevice
        ? Strings.Onb2_NoOutput
        : UiText.Format(Strings.Onb2_SystemFailed, SystemAudio.Error ?? string.Empty);

    /// <summary>Inline error of opening the Windows settings, or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLauncherError))]
    public partial string? LauncherError { get; set; }

    public bool HasLauncherError => LauncherError is not null;

    /// <summary>Both sources have been asked to start or stop (for tests).</summary>
    internal Task Operations => Task.WhenAll(Microphone.LastOperation, SystemAudio.LastOperation);

    internal override Task Settled => Operations;

    internal override void Activate()
    {
        _devices.Refresh();
        _active = true;
        LauncherError = null;
        _ = Microphone.StartAsync();
        _ = SystemAudio.StartAsync();
    }

    internal override void Deactivate()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        _ = Microphone.StopAsync();
        _ = SystemAudio.StopAsync();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        LauncherError = _launcher.TryOpenMicrophoneSettings() is { } failure
            ? UiText.Format(Strings.Onb2_SettingsFailed, failure)
            : null;
    }

    [RelayCommand]
    private void TryAgain()
    {
        if (_active)
        {
            LauncherError = null;
            _ = Microphone.StartAsync();
        }
    }

    private void OnMicrophoneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AudioChannelViewModel.State) or nameof(AudioChannelViewModel.Error))
        {
            OnPropertyChanged(nameof(ShowGranted));
            OnPropertyChanged(nameof(ShowDenied));
            OnPropertyChanged(nameof(ShowNoMicrophone));
            OnPropertyChanged(nameof(ShowMicrophoneFailed));
            OnPropertyChanged(nameof(MicrophoneFailedText));
            UpdateFooter();
        }
    }

    private void OnSystemAudioChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AudioChannelViewModel.State) or nameof(AudioChannelViewModel.Error))
        {
            OnPropertyChanged(nameof(ShowSystemUnavailable));
            OnPropertyChanged(nameof(SystemUnavailableText));
        }
    }

    private void OnDevicesChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Another device was picked: the meter follows it. (Refresh() re-selects the saved ones silently, before Activate starts.)
        if (!_active)
        {
            return;
        }

        if (e.PropertyName == nameof(DevicesSettingsViewModel.SelectedMicrophone))
        {
            _ = Microphone.StartAsync();
        }
        else if (e.PropertyName == nameof(DevicesSettingsViewModel.SelectedSystemDevice))
        {
            _ = SystemAudio.StartAsync();
        }
    }

    private void UpdateFooter()
    {
        var state = Microphone.State;
        CanContinue = state == AudioChannelState.Live;
        ShowSecondary = state is AudioChannelState.Denied or AudioChannelState.NoDevice or AudioChannelState.Failed;
        SecondaryLabel = Strings.Onb2_ContinueWithoutMic;
    }
}
