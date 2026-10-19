using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.App.Resources;
using Murmur.Core.Capture;
using Murmur.Core.Settings;

namespace Murmur.App.ViewModels;

/// <summary>One entry of a device or display picker; <c>Id == null</c> is "the system default".</summary>
public sealed record ChoiceItem(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The "Пристрої" section: the microphone, the system-audio output and the display the recording screen starts with,
/// and whether the screen is recorded by default. Every change is saved at once.
/// </summary>
public sealed partial class DevicesSettingsViewModel : ObservableObject
{
    private readonly ICapture _capture;
    private readonly SettingsWriter _writer;
    private bool _loading;

    internal DevicesSettingsViewModel(ICapture capture, SettingsWriter writer)
    {
        _capture = capture;
        _writer = writer;
        Refresh();
    }

    [ObservableProperty]
    public partial IReadOnlyList<ChoiceItem> Microphones { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ChoiceItem> SystemDevices { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ChoiceItem> Displays { get; set; } = [];

    [ObservableProperty]
    public partial ChoiceItem? SelectedMicrophone { get; set; }

    [ObservableProperty]
    public partial ChoiceItem? SelectedSystemDevice { get; set; }

    [ObservableProperty]
    public partial ChoiceItem? SelectedDisplay { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScreenByDefaultLabel))]
    public partial bool ScreenByDefault { get; set; }

    /// <summary>Inline error of the last save, or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    public bool MicrophoneAvailable => _capture.Capabilities.Microphone;

    public bool SystemAudioAvailable => _capture.Capabilities.SystemAudio;

    public bool ScreenAvailable => _capture.Capabilities.Screen;

    public bool ScreenUnavailable => !ScreenAvailable;

    public string ScreenByDefaultLabel => ScreenByDefault ? Strings.Devices_On : Strings.Devices_Off;

    /// <summary>Re-reads the devices and displays (they come and go) and the saved choices.</summary>
    internal void Refresh()
    {
        var settings = _writer.Current;
        _loading = true;
        try
        {
            Microphones = Build(_capture.Capabilities.Microphone ? _capture.GetAudioDevices(AudioDeviceKind.Microphone) : [], Strings.Devices_SystemDefault);
            SystemDevices = Build(_capture.Capabilities.SystemAudio ? _capture.GetAudioDevices(AudioDeviceKind.SystemOutput) : [], Strings.Devices_SystemDefault);
            Displays = BuildDisplays(_capture.Capabilities.Screen ? _capture.GetDisplays() : []);
            SelectedMicrophone = Find(Microphones, settings.MicrophoneDeviceId);
            SelectedSystemDevice = Find(SystemDevices, settings.SystemAudioDeviceId);
            SelectedDisplay = Find(Displays, settings.ScreenDisplayId);
            ScreenByDefault = settings.ScreenByDefault;
            Error = null;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Re-selects the saved choices in the current lists (the store changed elsewhere); no enumeration, the inline error stays.</summary>
    internal void ReloadSaved()
    {
        var settings = _writer.Current;
        _loading = true;
        try
        {
            SelectedMicrophone = Find(Microphones, settings.MicrophoneDeviceId);
            SelectedSystemDevice = Find(SystemDevices, settings.SystemAudioDeviceId);
            SelectedDisplay = Find(Displays, settings.ScreenDisplayId);
            ScreenByDefault = settings.ScreenByDefault;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnSelectedMicrophoneChanged(ChoiceItem? value)
    {
        if (!_loading && value is not null)
        {
            Save(s => s with { MicrophoneDeviceId = value.Id });
        }
    }

    partial void OnSelectedSystemDeviceChanged(ChoiceItem? value)
    {
        if (!_loading && value is not null)
        {
            Save(s => s with { SystemAudioDeviceId = value.Id });
        }
    }

    partial void OnSelectedDisplayChanged(ChoiceItem? value)
    {
        if (!_loading && value is not null)
        {
            Save(s => s with { ScreenDisplayId = value.Id });
        }
    }

    partial void OnScreenByDefaultChanged(bool value)
    {
        if (!_loading)
        {
            Save(s => s with { ScreenByDefault = value });
        }
    }

    /// <summary>The last save started (for tests).</summary>
    internal Task SaveTask { get; private set; } = Task.CompletedTask;

    private void Save(Func<AppSettings, AppSettings> edit) => SaveTask = SaveAsync(edit);

    private async Task SaveAsync(Func<AppSettings, AppSettings> edit)
    {
        Error = await _writer.UpdateAsync(edit).ConfigureAwait(true);
    }

    private static List<ChoiceItem> Build(IReadOnlyList<AudioDevice> devices, string defaultName)
    {
        var items = new List<ChoiceItem>(devices.Count + 1) { new(null, defaultName) };
        foreach (var device in devices)
        {
            items.Add(new ChoiceItem(device.Id, device.Name));
        }

        return items;
    }

    private static List<ChoiceItem> BuildDisplays(IReadOnlyList<DisplayInfo> displays)
    {
        var items = new List<ChoiceItem>(displays.Count + 1) { new(null, Strings.Devices_PrimaryDisplay) };
        foreach (var display in displays)
        {
            items.Add(new ChoiceItem(display.Id, UiText.Format(Strings.Devices_Display, display.Name, display.Width, display.Height)));
        }

        return items;
    }

    /// <summary>The saved choice; a device that is not plugged in any more shows as the default (the saved id stays until the user picks something).</summary>
    private static ChoiceItem Find(IReadOnlyList<ChoiceItem> items, string? id)
    {
        foreach (var item in items)
        {
            if (item.Id is not null && string.Equals(item.Id, id, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return items[0];
    }
}
