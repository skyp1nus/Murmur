using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Murmur.App.Services;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

public partial class SettingsView : UserControl
{
    /// <summary>The room kept above a section the sidebar scrolled to (its title does not touch the top bar).</summary>
    private const double SectionAir = 24;

    /// <summary>A section counts as the one in view once its top is this close to the top edge or above it.</summary>
    private const double ReadLine = 96;

    private SettingsViewModel? _subscribed;
    private string? _pinned;
    private double _pinnedOffset;

    public SettingsView()
    {
        InitializeComponent();

        // Capturing a hotkey: the next key press anywhere in the screen belongs to the row that waits for it. Tunnelling
        // runs before the focused button sees Space or Enter, so those can be part of a combination too.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);
        Scroller.ScrollChanged += OnScrollChanged;
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

    /// <summary>The sections in page order with their ids (the Models section is hidden without a model manager).</summary>
    private (string Id, Control Section)[] Sections() =>
    [
        (SettingsViewModel.ModelsSectionId, ModelsSection),
        (SettingsViewModel.DevicesSectionId, DevicesSection),
        (SettingsViewModel.StorageSectionId, StorageSection),
        (SettingsViewModel.HotkeysSectionId, HotkeysSection),
        (SettingsViewModel.AboutSectionId, AboutSection),
        (SettingsViewModel.AppearanceSectionId, AppearanceSection),
    ];

    private double TopOf(Control section) => section.TranslatePoint(default, Page)?.Y ?? 0;

    /// <summary>A link of the sidebar: scrolls the section to the top (as far as the page can scroll) and marks it as the one in view until the user scrolls on.</summary>
    private void OnSectionRequested(string id)
    {
        foreach (var (sectionId, section) in Sections())
        {
            if (sectionId != id || !section.IsVisible)
            {
                continue;
            }

            Scroller.Offset = new Vector(0, Math.Max(0, TopOf(section) - SectionAir));
            _pinned = id;
            _pinnedOffset = Scroller.Offset.Y;
            if (_subscribed is not null)
            {
                _subscribed.CurrentSectionId = id;
            }

            return;
        }
    }

    /// <summary>
    /// The section in view: the last one whose top has reached the read line; none while the page rests at its top; the last one
    /// at the bottom edge. After a click on the sidebar the clicked section stays marked until the page moves from where the click put it.
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_subscribed is null)
        {
            return;
        }

        var offset = Scroller.Offset.Y;
        if (_pinned is not null)
        {
            if (Math.Abs(offset - _pinnedOffset) < 1.5)
            {
                return;
            }

            _pinned = null;
        }

        if (offset < 1)
        {
            _subscribed.CurrentSectionId = null;
            return;
        }

        string? current = null;
        var atBottom = offset + Scroller.Viewport.Height >= Scroller.Extent.Height - 1;
        foreach (var (id, section) in Sections())
        {
            if (!section.IsVisible)
            {
                continue;
            }

            if (atBottom || TopOf(section) <= offset + ReadLine)
            {
                current = id;
            }
        }

        _subscribed.CurrentSectionId = current;
    }
}
