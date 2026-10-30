using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.Core.Capture;
using Murmur.Core.Intelligence;
using Murmur.Core.Models;
using Murmur.Core.Settings;

namespace Murmur.App.ViewModels;

/// <summary>One link of the section list in the sidebar; <see cref="Id"/> names the section for <see cref="SettingsViewModel.SectionRequested"/>.</summary>
public sealed record SettingsSection(string Id, string Title);

/// <summary>
/// The Settings screen: models, devices, storage, hotkeys, appearance and "about". Every change is saved through
/// <see cref="ISettingsStore.SaveAsync"/> immediately; errors are shown inline in the section that caused them.
/// </summary>
/// <remarks>
/// <para><b>Lazy sections.</b> Building the section view models is the expensive part of the screen (device enumeration,
/// the status of every catalog item, free space, subscriptions), so each one is created on first use: when the Settings
/// are first shown, or when the onboarding, which shares them, first needs one. The section list for the sidebar needs
/// nothing but the ids and titles.</para>
/// <para><b>Follows the store.</b> A change saved by anyone else (the onboarding, the hotkey registration) is re-read into
/// the sections that exist; the event is marshalled to the UI thread. <see cref="Dispose"/> unsubscribes.</para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable, IFillsFrame
{
    public const string ModelsSectionId = "models";
    public const string DevicesSectionId = "devices";
    public const string StorageSectionId = "storage";
    public const string HotkeysSectionId = "hotkeys";
    public const string AppearanceSectionId = "appearance";
    public const string AboutSectionId = "about";

    private readonly ISettingsStore _store;
    private readonly SettingsWriter _writer;
    private readonly ICapture _capture;
    private readonly IModelManager? _modelManager;
    private readonly Func<ModelKind, string?>? _activeModel;
    private readonly string? _defaultRecordingsDirectory;
    private readonly IFolderLauncher _launcher;
    private readonly Action<Action> _post;
    private readonly IAccelerationProbe? _accelerationProbe;
    private readonly IAccelerationStatusSource? _accelerationStatus;
    private readonly IReadOnlyDictionary<AiAcceleration, string>? _accelerationRuntimeIds;
    private ModelsSettingsViewModel? _models;
    private AccelerationSettingsViewModel? _acceleration;
    private DevicesSettingsViewModel? _devices;
    private StorageSettingsViewModel? _storage;
    private HotkeysSettingsViewModel? _hotkeys;
    private AppearanceSettingsViewModel? _appearance;
    private bool _disposed;

    /// <param name="settings">Where preferences are read and saved.</param>
    /// <param name="capture">Lists the audio devices and displays.</param>
    /// <param name="models">The model manager; <c>null</c> hides the "Моделі" section.</param>
    /// <param name="activeModel">The chat/embedding model active on disk, see <see cref="AppServices.ActiveModelId"/>.</param>
    /// <param name="defaultRecordingsDirectory">Shown while no folder is chosen.</param>
    /// <param name="launcher">Opens the recordings folder; the OS shell by default.</param>
    /// <param name="post">Marshals model-manager and settings-store events to the UI thread; the UI dispatcher by default.</param>
    /// <param name="version">The version shown under "About"; the build-time version by default.</param>
    /// <param name="accelerationProbe">Which accelerations this PC supports; <c>null</c> = only the CPU build is offered.</param>
    /// <param name="accelerationStatus">Which AI build runs and why a GPU build does not; <c>null</c> = nothing to report.</param>
    /// <param name="accelerationRuntimeIds">The model-manager item of each GPU build's runtime (<see cref="AiServices.AccelerationRuntimeIds"/>); <c>null</c> = nothing to download.</param>
    public SettingsViewModel(
        ISettingsStore settings,
        ICapture capture,
        IModelManager? models = null,
        Func<ModelKind, string?>? activeModel = null,
        string? defaultRecordingsDirectory = null,
        IFolderLauncher? launcher = null,
        Action<Action>? post = null,
        string? version = null,
        IAccelerationProbe? accelerationProbe = null,
        IAccelerationStatusSource? accelerationStatus = null,
        IReadOnlyDictionary<AiAcceleration, string>? accelerationRuntimeIds = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(capture);
        _store = settings;
        _writer = SettingsWriter.For(settings);
        _capture = capture;
        _modelManager = models;
        _activeModel = activeModel;
        _defaultRecordingsDirectory = defaultRecordingsDirectory;
        _launcher = launcher ?? SystemFolderLauncher.Instance;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        _accelerationProbe = accelerationProbe;
        _accelerationStatus = accelerationStatus;
        _accelerationRuntimeIds = accelerationRuntimeIds;
        About = new AboutViewModel(version);

        var sections = new List<SettingsSection>(6);
        if (models is not null)
        {
            sections.Add(new SettingsSection(ModelsSectionId, Strings.Settings_ModelsTitle));
        }

        sections.Add(new SettingsSection(DevicesSectionId, Strings.Settings_DevicesTitle));
        sections.Add(new SettingsSection(StorageSectionId, Strings.Settings_StorageTitle));
        sections.Add(new SettingsSection(HotkeysSectionId, Strings.Settings_HotkeysTitle));
        sections.Add(new SettingsSection(AboutSectionId, Strings.Settings_AboutTitle));

        // Not on the canvas: the preferences the canvas has no room for follow the five drawn sections.
        sections.Add(new SettingsSection(AppearanceSectionId, Strings.Settings_AppearanceTitle));
        Sections = sections;
        _store.Changed += OnStoreChanged;
    }

    /// <summary>Created on first use; <c>null</c> when no model manager is wired.</summary>
    public ModelsSettingsViewModel? Models =>
        _models ??= _modelManager is null || _disposed ? null : new ModelsSettingsViewModel(_modelManager, _writer, _activeModel, _post);

    public bool HasModels => _modelManager is not null;

    /// <summary>The Acceleration card of the Models section; created on first use, <c>null</c> when no model manager is wired.</summary>
    public AccelerationSettingsViewModel? Acceleration =>
        _acceleration ??= _modelManager is null || _disposed
            ? null
            : new AccelerationSettingsViewModel(_writer, _modelManager, _accelerationProbe, _accelerationStatus, _accelerationRuntimeIds, _post);

    /// <summary>Created on first use.</summary>
    public DevicesSettingsViewModel Devices => _devices ?? (CanCreate() ? _devices = new DevicesSettingsViewModel(_capture, _writer) : null!);

    /// <summary>Created on first use.</summary>
    public StorageSettingsViewModel Storage => _storage ?? (CanCreate() ? _storage = new StorageSettingsViewModel(_writer, _defaultRecordingsDirectory, _launcher) : null!);

    /// <summary>Created on first use.</summary>
    public HotkeysSettingsViewModel Hotkeys => _hotkeys ?? (CanCreate() ? _hotkeys = new HotkeysSettingsViewModel(_writer) : null!);

    /// <summary>Created on first use.</summary>
    public AppearanceSettingsViewModel Appearance => _appearance ?? (CanCreate() ? _appearance = new AppearanceSettingsViewModel(_writer) : null!);

    public AboutViewModel About { get; }

    public IReadOnlyList<SettingsSection> Sections { get; }

    /// <summary>A section that does not exist yet is never created after <see cref="Dispose"/> (it would subscribe to things nobody releases).</summary>
    private bool CanCreate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return true;
    }

    /// <summary>Which section view models exist yet (for tests of the lazy creation).</summary>
    internal bool AreSectionsCreated => _acceleration is not null || _devices is not null || _models is not null || _storage is not null || _hotkeys is not null || _appearance is not null;

    /// <summary>The last settings change started by any section (for tests).</summary>
    internal Task LastSave => _writer.LastSave;

    /// <summary>The one writer of every settings change; the onboarding saves through it so its edits never overlap the sections' own.</summary>
    internal SettingsWriter Writer => _writer;

    /// <summary>A link of the sidebar was clicked: the view scrolls the section to the top of the page.</summary>
    public event Action<string>? SectionRequested;

    /// <summary>
    /// The section in view (an id of <see cref="Sections"/>), reported by the view while the page scrolls; <c>null</c> while the page
    /// rests at its top and nothing was asked for (the canvas highlights no section then). The sidebar highlights it.
    /// </summary>
    [ObservableProperty]
    public partial string? CurrentSectionId { get; set; }

    /// <summary>Re-reads devices, model states, free space and the saved values; called every time the screen is shown.</summary>
    public void Refresh()
    {
        CurrentSectionId = null;
        // A section that was never needed has nothing to refresh: its first use reads everything.
        _models?.Refresh();
        _acceleration?.Refresh();
        _devices?.Refresh();
        _storage?.Refresh();
        _hotkeys?.Refresh();
        _appearance?.Refresh();
    }

    private void OnStoreChanged(AppSettings settings) => _post(() =>
    {
        if (_disposed)
        {
            return;
        }

        // Only the saved values are re-read (ReloadSaved): no device enumeration, and no inline error, hotkey registration
        // status or capture in progress is lost. The full Refresh() is for showing the screen.
        _models?.RefreshActive();
        _acceleration?.ReloadSaved();
        _devices?.ReloadSaved();
        _storage?.Refresh();
        _hotkeys?.ReloadSaved();
        _appearance?.ReloadSaved();
    });

    [RelayCommand]
    private void ShowSection(SettingsSection? section)
    {
        if (section is not null)
        {
            SectionRequested?.Invoke(section.Id);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Changed -= OnStoreChanged;
        _models?.Dispose();
        _acceleration?.Dispose();
    }
}
