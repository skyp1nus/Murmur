using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Murmur.App.Services;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _subscribed;

    public SettingsView()
    {
        InitializeComponent();

        // Capturing a hotkey: the next key press anywhere in the screen belongs to the row that waits for it. Tunnelling
        // runs before the focused button sees Space or Enter, so those can be part of a combination too.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribed is not null)
        {
            _subscribed.SectionRequested -= OnSectionRequested;
        }

        _subscribed = DataContext as SettingsViewModel;
        if (_subscribed is not null)
        {
            _subscribed.SectionRequested += OnSectionRequested;
            _subscribed.Storage.FolderPicker = new TopLevelFolderPicker(() => TopLevel.GetTopLevel(this));
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && vm.Hotkeys.HandleKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        // Clicking away from the key caps cancels the capture. Moving to another row's caps starts that row's capture
        // afterwards (the click comes after the focus change).
        if (DataContext is SettingsViewModel vm && e.Source is Button button && button.Classes.Contains("keycap"))
        {
            vm.Hotkeys.CancelCapture();
        }
    }

    private void OnSectionRequested(string id)
    {
        Control? section = id switch
        {
            SettingsViewModel.ModelsSectionId => ModelsSection,
            SettingsViewModel.DevicesSectionId => DevicesSection,
            SettingsViewModel.StorageSectionId => StorageSection,
            SettingsViewModel.HotkeysSectionId => HotkeysSection,
            SettingsViewModel.AppearanceSectionId => AppearanceSection,
            SettingsViewModel.AboutSectionId => AboutSection,
            _ => null,
        };

        section?.BringIntoView();
    }
}
