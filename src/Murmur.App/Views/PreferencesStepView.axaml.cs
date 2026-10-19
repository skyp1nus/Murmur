using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Murmur.App.Services;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

public partial class PreferencesStepView : UserControl
{
    public PreferencesStepView()
    {
        InitializeComponent();

        // The same key capture as the Settings: while the hotkey field waits, the next key press belongs to it. Tunnelling runs
        // before the focused button sees Space or Enter, so those can be part of a combination too.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is PreferencesStepViewModel vm)
        {
            vm.Storage.FolderPicker = new TopLevelFolderPicker(() => TopLevel.GetTopLevel(this));
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is PreferencesStepViewModel vm && vm.Hotkeys.HandleKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        // Clicking away from the field cancels the capture.
        if (DataContext is PreferencesStepViewModel vm && e.Source is Button button && button.Classes.Contains("hotkeyField"))
        {
            vm.Hotkeys.CancelCapture();
        }
    }
}
