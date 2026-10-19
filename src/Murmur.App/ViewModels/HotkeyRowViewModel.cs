using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Input;

namespace Murmur.App.ViewModels;

public enum HotkeyAction
{
    Record,
    ToggleScreen,
    ShowApp,
}

/// <summary>One row of the "Гарячі клавіші" card: the action, its key caps and the capture state.</summary>
public sealed partial class HotkeyRowViewModel : ObservableObject
{
    private readonly HotkeysSettingsViewModel _owner;

    internal HotkeyRowViewModel(HotkeyAction action, string label, string? gesture, HotkeysSettingsViewModel owner)
    {
        Action = action;
        Label = label;
        Gesture = gesture;
        _owner = owner;
    }

    public HotkeyAction Action { get; }

    public string Label { get; }

    /// <summary>The stored gesture (<c>Ctrl+Shift+R</c>), or <c>null</c> = none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    public partial string? Gesture { get; set; }

    /// <summary>Waiting for the next key combination.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText), nameof(ShowHint), nameof(ShowRegistrationNote))]
    public partial bool IsCapturing { get; set; }

    /// <summary>Why the last combination was refused, or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowRegistrationNote))]
    public partial string? Error { get; set; }

    /// <summary>
    /// What the system said about the stored gesture the last time it was registered as a global hotkey
    /// (<see cref="HotkeyRegistration.Registered"/> until the app has tried).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RegistrationNote), nameof(HasRegistrationNote), nameof(ShowRegistrationNote))]
    public partial HotkeyRegistration Registration { get; set; }

    public bool HasError => Error is not null;

    public bool ShowHint => IsCapturing;

    /// <summary>
    /// A short note under the row when the gesture does not work as a global hotkey: taken by another app, not usable, or not
    /// supported on this system. <c>null</c> when it works or there is none.
    /// </summary>
    public string? RegistrationNote => Registration switch
    {
        HotkeyRegistration.Taken => Strings.Hotkeys_Taken,
        HotkeyRegistration.Invalid => Strings.Hotkeys_Invalid,
        HotkeyRegistration.Unsupported => Strings.Hotkeys_Unsupported,
        _ => null,
    };

    public bool HasRegistrationNote => RegistrationNote is not null;

    /// <summary>The note is shown unless the row is capturing or shows a refusal of its own (they take the same line).</summary>
    public bool ShowRegistrationNote => HasRegistrationNote && !IsCapturing && !HasError;

    public string Hint => Strings.Hotkeys_Hint;

    /// <summary>The key caps: <c>Ctrl Shift R</c>, "Натисніть комбінацію…" while capturing, "Не задано" when empty.</summary>
    public string DisplayText => IsCapturing ? Strings.Hotkeys_Press : Gesture is { Length: > 0 } g ? HotkeyGestures.Display(g) : Strings.Hotkeys_None;

    [RelayCommand]
    private void Capture() => _owner.BeginCapture(this);
}
