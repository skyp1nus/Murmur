using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.Core.Input;

namespace Murmur.App.ViewModels;

/// <summary>One cap of the hotkey field: a key name and whether a "+" follows it.</summary>
public sealed record HotkeyCap(string Text, bool HasPlus);

/// <summary>
/// Step 4: transcription language (only "Auto-detect" for now), the recordings folder with its free space, the start/stop
/// hotkey and whether the screen is recorded by default. The folder, the hotkey and the toggle are the Settings' own view
/// models, so a change is saved at once and behaves exactly like it does there.
/// </summary>
public sealed partial class PreferencesStepViewModel : OnboardingStepViewModel
{
    private readonly Func<string?, long?> _freeSpace;

    internal PreferencesStepViewModel(SettingsViewModel settings, Func<string?, long?>? freeSpace = null)
    {
        Storage = settings.Storage;
        Hotkeys = settings.Hotkeys;
        Devices = settings.Devices;
        HotkeyRow = settings.Hotkeys.Rows.FirstOrDefault(r => r.Action == HotkeyAction.ToggleRecording);
        _freeSpace = freeSpace ?? DiskSpace.TryGetFree;
        Storage.PropertyChanged += OnStorageChanged;
        if (HotkeyRow is not null)
        {
            HotkeyRow.PropertyChanged += OnHotkeyChanged;
        }

        // The drive is asked when the step opens, not while the window starts.
    }

    public override string Title => Strings.Onb4_Headline;

    public override string Subtitle => Strings.Onb4_Subline;

    public StorageSettingsViewModel Storage { get; }

    public HotkeysSettingsViewModel Hotkeys { get; }

    public DevicesSettingsViewModel Devices { get; }

    /// <summary>The start/stop row of the Settings' hotkeys; <c>null</c> when they have none (the field is then hidden).</summary>
    public HotkeyRowViewModel? HotkeyRow { get; }

    public bool HasHotkey => HotkeyRow is not null;

    /// <summary>The only language of the MVP: Parakeet detects it by itself.</summary>
    public IReadOnlyList<string> Languages { get; } = [Strings.Onb4_Auto];

    /// <summary>"123.46 GB free" for the drive of the recordings folder, or empty when unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFreeSpace))]
    public partial string FreeSpaceText { get; set; } = string.Empty;

    public bool HasFreeSpace => FreeSpaceText.Length > 0;

    /// <summary>The caps of the saved hotkey, e.g. Ctrl + Shift + R; empty while none is set or one is being captured.</summary>
    public IReadOnlyList<HotkeyCap> Caps { get; private set; } = [];

    public bool ShowCaps => HotkeyRow is { IsCapturing: false } && Caps.Count > 0;

    /// <summary>"Not set" while no hotkey is saved.</summary>
    public bool ShowNone => HotkeyRow is { IsCapturing: false } && Caps.Count == 0;

    internal override void Activate()
    {
        Storage.Refresh();
        Hotkeys.Refresh();
        Devices.Refresh();
        RefreshFreeSpace();
        RefreshCaps();
    }

    internal override void Deactivate() => Hotkeys.CancelCapture();

    private void OnStorageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StorageSettingsViewModel.EffectivePath))
        {
            RefreshFreeSpace();
        }
    }

    private void OnHotkeyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HotkeyRowViewModel.Gesture) or nameof(HotkeyRowViewModel.IsCapturing))
        {
            RefreshCaps();
        }
    }

    private void RefreshFreeSpace()
    {
        FreeSpaceText = _freeSpace(Storage.EffectivePath) is { } free
            ? UiText.Format(Strings.Onb4_FreeSpace, UiFormat.Size(free))
            : string.Empty;
    }

    private void RefreshCaps()
    {
        var gesture = HotkeyRow?.Gesture;
        if (string.IsNullOrEmpty(gesture))
        {
            Caps = [];
        }
        else
        {
            var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var caps = new HotkeyCap[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                caps[i] = new HotkeyCap(parts[i], i < parts.Length - 1);
            }

            Caps = caps;
        }

        OnPropertyChanged(nameof(Caps));
        OnPropertyChanged(nameof(ShowCaps));
        OnPropertyChanged(nameof(ShowNone));
    }
}
