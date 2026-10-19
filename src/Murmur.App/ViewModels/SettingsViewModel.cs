using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.Core.Capture;
using Murmur.Core.Models;
using Murmur.Core.Settings;

namespace Murmur.App.ViewModels;

/// <summary>One link of the section list in the sidebar; <see cref="Id"/> names the section for <see cref="SettingsViewModel.SectionRequested"/>.</summary>
public sealed record SettingsSection(string Id, string Title);

/// <summary>
/// The Settings screen: models, devices, storage, hotkeys, appearance and "about". Every change is saved through
/// <see cref="ISettingsStore.SaveAsync"/> immediately; errors are shown inline in the section that caused them.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    public const string ModelsSectionId = "models";
    public const string DevicesSectionId = "devices";
    public const string StorageSectionId = "storage";
    public const string HotkeysSectionId = "hotkeys";
    public const string AppearanceSectionId = "appearance";
    public const string AboutSectionId = "about";

    private readonly SettingsWriter _writer;

    /// <param name="settings">Where preferences are read and saved.</param>
    /// <param name="capture">Lists the audio devices and displays.</param>
    /// <param name="models">The model manager; <c>null</c> hides the "Моделі" section.</param>
    /// <param name="activeModel">The chat/embedding model active on disk, see <see cref="AppServices.ActiveModelId"/>.</param>
    /// <param name="defaultRecordingsDirectory">Shown while no folder is chosen.</param>
    /// <param name="launcher">Opens the recordings folder; the OS shell by default.</param>
    /// <param name="post">Marshals model-manager events to the UI thread; the UI dispatcher by default.</param>
    /// <param name="version">The version shown under "Про програму"; the program's informational version by default.</param>
    public SettingsViewModel(
        ISettingsStore settings,
        ICapture capture,
        IModelManager? models = null,
        Func<ModelKind, string?>? activeModel = null,
        string? defaultRecordingsDirectory = null,
        IFolderLauncher? launcher = null,
        Action<Action>? post = null,
        string? version = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(capture);
        _writer = new SettingsWriter(settings);
        post ??= action => Avalonia.Threading.Dispatcher.UIThread.Post(action);

        Models = models is null ? null : new ModelsSettingsViewModel(models, _writer, activeModel, post);
        Devices = new DevicesSettingsViewModel(capture, _writer);
        Storage = new StorageSettingsViewModel(_writer, defaultRecordingsDirectory, launcher ?? SystemFolderLauncher.Instance);
        Hotkeys = new HotkeysSettingsViewModel(_writer);
        Appearance = new AppearanceSettingsViewModel(_writer);
        About = new AboutViewModel(version);

        var sections = new List<SettingsSection>(6);
        if (Models is not null)
        {
            sections.Add(new SettingsSection(ModelsSectionId, Strings.Settings_ModelsTitle));
        }

        sections.Add(new SettingsSection(DevicesSectionId, Strings.Settings_DevicesTitle));
        sections.Add(new SettingsSection(StorageSectionId, Strings.Settings_StorageTitle));
        sections.Add(new SettingsSection(HotkeysSectionId, Strings.Settings_HotkeysTitle));
        sections.Add(new SettingsSection(AppearanceSectionId, Strings.Settings_AppearanceTitle));
        sections.Add(new SettingsSection(AboutSectionId, Strings.Settings_AboutTitle));
        Sections = sections;
    }

    /// <summary><c>null</c> when no model manager is wired.</summary>
    public ModelsSettingsViewModel? Models { get; }

    public bool HasModels => Models is not null;

    public DevicesSettingsViewModel Devices { get; }

    public StorageSettingsViewModel Storage { get; }

    public HotkeysSettingsViewModel Hotkeys { get; }

    public AppearanceSettingsViewModel Appearance { get; }

    public AboutViewModel About { get; }

    public IReadOnlyList<SettingsSection> Sections { get; }

    /// <summary>The last settings change started by any section (for tests).</summary>
    internal Task LastSave => _writer.LastSave;

    /// <summary>The one writer of every settings change; the onboarding saves through it so its edits never overlap the sections' own.</summary>
    internal SettingsWriter Writer => _writer;

    /// <summary>A link of the sidebar was clicked: the view scrolls the section into view.</summary>
    public event Action<string>? SectionRequested;

    /// <summary>Re-reads devices, model states, free space and the saved values; called every time the screen is shown.</summary>
    public void Refresh()
    {
        Models?.Refresh();
        Devices.Refresh();
        Storage.Refresh();
        Hotkeys.Refresh();
        Appearance.Refresh();
    }

    [RelayCommand]
    private void ShowSection(SettingsSection? section)
    {
        if (section is not null)
        {
            SectionRequested?.Invoke(section.Id);
        }
    }

    public void Dispose() => Models?.Dispose();
}
