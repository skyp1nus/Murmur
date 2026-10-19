using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.App.Resources;
using Murmur.Core.Input;
using Murmur.Core.Settings;

namespace Murmur.App.ViewModels;

/// <summary>
/// The "Гарячі клавіші" section. A click on a row starts capturing: the next key combination becomes the hotkey, Esc
/// cancels, Backspace clears it. Combinations are validated with a <c>KeyGesture.Parse</c> round trip and a combination that
/// another action already uses is refused. The app registers the stored gestures with the system
/// (<c>HotkeyCoordinator</c>) and reports each result back through <see cref="SetRegistration"/>, which shows as a note
/// under the row.
/// </summary>
public sealed partial class HotkeysSettingsViewModel : ObservableObject
{
    private readonly SettingsWriter _writer;

    internal HotkeysSettingsViewModel(SettingsWriter writer)
    {
        _writer = writer;
        Rows =
        [
            new HotkeyRowViewModel(HotkeyAction.ToggleRecording, Strings.Hotkeys_Record, null, this),
            new HotkeyRowViewModel(HotkeyAction.ToggleScreen, Strings.Hotkeys_ToggleScreen, null, this),
            new HotkeyRowViewModel(HotkeyAction.ShowApp, Strings.Hotkeys_ShowApp, null, this),
        ];
        Refresh();
    }

    public IReadOnlyList<HotkeyRowViewModel> Rows { get; }

    /// <summary>The row waiting for a key combination, or <c>null</c>.</summary>
    public HotkeyRowViewModel? Capturing
    {
        get;
        private set
        {
            var was = field is not null;
            field = value;
            if (was != (value is not null))
            {
                CapturingChanged?.Invoke(value is not null);
            }
        }
    }

    /// <summary>
    /// A row started (<see langword="true"/>) or stopped (<see langword="false"/>) waiting for a key combination, on every way
    /// in and out: a click, Esc, Backspace, a saved combination, focus lost, <see cref="Refresh"/>. Moving from one row to
    /// another is not reported. The app releases its global hotkeys while this is true, so the combination reaches the field.
    /// </summary>
    public event Action<bool>? CapturingChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>The last save started (for tests).</summary>
    internal Task LastSave { get; private set; } = Task.CompletedTask;

    internal void Refresh()
    {
        var settings = _writer.Current;
        foreach (var row in Rows)
        {
            row.Gesture = GestureOf(settings, row.Action);
            row.IsCapturing = false;
            row.Error = null;
        }

        Capturing = null;
        Error = null;
    }

    /// <summary>The system's answer for the gesture of an action, shown as a note under its row (nothing for Registered and Cleared).</summary>
    internal void SetRegistration(HotkeyAction action, HotkeyRegistration registration)
    {
        foreach (var row in Rows)
        {
            if (row.Action == action)
            {
                row.Registration = registration;
            }
        }
    }

    internal void BeginCapture(HotkeyRowViewModel row)
    {
        if (Capturing is { } previous && previous != row)
        {
            previous.IsCapturing = false;
            previous.Error = null;
        }

        row.Error = null;
        row.IsCapturing = true;
        Capturing = row;
    }

    /// <summary>Stops capturing without changing anything (focus left, Esc).</summary>
    public void CancelCapture() => EndCapture();

    /// <summary>
    /// Feeds a key press to the row that is capturing. Returns <see langword="true"/> when the press was consumed (the view
    /// marks the event handled), <see langword="false"/> when nothing is capturing.
    /// </summary>
    public bool HandleKey(Key key, KeyModifiers modifiers)
    {
        if (Capturing is not { } row)
        {
            return false;
        }

        if (modifiers == KeyModifiers.None && key == Key.Escape)
        {
            EndCapture();
            return true;
        }

        if (modifiers == KeyModifiers.None && key == Key.Back)
        {
            EndCapture();
            Save(row, null);
            return true;
        }

        var gesture = HotkeyGestures.TryFormat(key, modifiers, out var problem);
        if (gesture is null)
        {
            // A modifier on its own is just the start of a combination.
            if (problem != HotkeyProblem.ModifierOnly)
            {
                row.Error = problem == HotkeyProblem.NeedsModifier ? Strings.Hotkeys_NeedModifier : Strings.Hotkeys_Invalid;
            }

            return true;
        }

        foreach (var other in Rows)
        {
            if (other != row && HotkeyGestures.Same(other.Gesture, gesture))
            {
                row.Error = UiText.Format(Strings.Hotkeys_Duplicate, other.Label);
                return true;
            }
        }

        EndCapture();
        Save(row, gesture);
        return true;
    }

    private void EndCapture()
    {
        if (Capturing is { } row)
        {
            row.IsCapturing = false;
            row.Error = null;
            Capturing = null;
        }
    }

    private void Save(HotkeyRowViewModel row, string? gesture)
    {
        // The note was about the old gesture; the app registers the new one and reports again. The same text keeps its note.
        if (!string.Equals(row.Gesture, gesture, StringComparison.Ordinal))
        {
            row.Registration = HotkeyRegistration.Registered;
        }

        row.Gesture = gesture;
        LastSave = SaveAsync(row.Action, gesture);
    }

    private async Task SaveAsync(HotkeyAction action, string? gesture)
    {
        Error = await _writer.UpdateAsync(s => With(s, action, gesture)).ConfigureAwait(true);
    }

    private static string? GestureOf(AppSettings settings, HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleRecording => settings.RecordHotkey,
        HotkeyAction.ToggleScreen => settings.ToggleScreenHotkey,
        _ => settings.ShowAppHotkey,
    };

    private static AppSettings With(AppSettings settings, HotkeyAction action, string? gesture) => action switch
    {
        HotkeyAction.ToggleRecording => settings with { RecordHotkey = gesture },
        HotkeyAction.ToggleScreen => settings with { ToggleScreenHotkey = gesture },
        _ => settings with { ShowAppHotkey = gesture },
    };
}
