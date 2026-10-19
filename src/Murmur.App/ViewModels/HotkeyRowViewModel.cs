using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;

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
    [NotifyPropertyChangedFor(nameof(DisplayText), nameof(ShowHint))]
    public partial bool IsCapturing { get; set; }

    /// <summary>Why the last combination was refused, or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    public bool ShowHint => IsCapturing;

    public string Hint => Strings.Hotkeys_Hint;

    /// <summary>The key caps: <c>Ctrl Shift R</c>, "Натисніть комбінацію…" while capturing, "Не задано" when empty.</summary>
    public string DisplayText => IsCapturing ? Strings.Hotkeys_Press : Gesture is { Length: > 0 } g ? HotkeyGestures.Display(g) : Strings.Hotkeys_None;

    [RelayCommand]
    private void Capture() => _owner.BeginCapture(this);
}
