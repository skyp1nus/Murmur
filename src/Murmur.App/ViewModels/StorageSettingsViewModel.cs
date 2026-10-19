using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.App.Services;

namespace Murmur.App.ViewModels;

/// <summary>The "Сховище" section: where new recordings go, with a folder picker and "open in the file manager".</summary>
public sealed partial class StorageSettingsViewModel : ObservableObject
{
    private readonly SettingsWriter _writer;
    private readonly string? _defaultDirectory;
    private readonly IFolderLauncher _launcher;

    internal StorageSettingsViewModel(SettingsWriter writer, string? defaultDirectory, IFolderLauncher launcher)
    {
        _writer = writer;
        _defaultDirectory = defaultDirectory;
        _launcher = launcher;
        Refresh();
    }

    /// <summary>Set by the view: opens the OS folder dialog over its window. Without it "Змінити" does nothing.</summary>
    public IFolderPicker? FolderPicker { get; set; }

    /// <summary>The folder new recordings go to: the saved one, else the default data folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpen))]
    public partial string EffectivePath { get; set; } = string.Empty;

    /// <summary>The folder is the default one (nothing saved).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReset))]
    public partial bool IsDefaultFolder { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    public bool CanOpen => EffectivePath.Length > 0;

    public bool CanReset => !IsDefaultFolder;

    /// <summary>The last change started (for tests).</summary>
    internal Task LastOperation { get; private set; } = Task.CompletedTask;

    internal void Refresh()
    {
        var saved = _writer.Current.RecordingsDirectory;
        IsDefaultFolder = string.IsNullOrWhiteSpace(saved);
        EffectivePath = IsDefaultFolder ? _defaultDirectory ?? string.Empty : saved!;
    }

    [RelayCommand]
    private Task ChangeAsync() => LastOperation = ChangeCoreAsync();

    [RelayCommand]
    private Task ResetAsync() => LastOperation = SaveAsync(null);

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open()
    {
        Error = null;
        try
        {
            Directory.CreateDirectory(EffectivePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Error = UiText.Format(Strings.Storage_OpenFailed, ex.Message);
            return;
        }

        if (_launcher.TryOpen(EffectivePath) is { } failure)
        {
            Error = UiText.Format(Strings.Storage_OpenFailed, failure);
        }
    }

    partial void OnEffectivePathChanged(string value) => OpenCommand.NotifyCanExecuteChanged();

    private async Task ChangeCoreAsync()
    {
        if (FolderPicker is not { } picker)
        {
            return;
        }

        Error = null;
        var chosen = await picker.PickFolderAsync(Strings.Storage_PickerTitle, EffectivePath).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            await SaveAsync(chosen).ConfigureAwait(true);
        }
    }

    private async Task SaveAsync(string? directory)
    {
        Error = await _writer.UpdateAsync(s => s with { RecordingsDirectory = directory }).ConfigureAwait(true);
        Refresh();
    }
}
