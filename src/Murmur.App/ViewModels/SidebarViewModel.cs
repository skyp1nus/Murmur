using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Murmur.App.ViewModels;

/// <summary>
/// The left navigation of the mockups: new recording, Library, Ask AI, Settings, and (on the Settings screen) the section
/// list. It follows the page the shell shows.
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    internal SidebarViewModel(ShellViewModel shell)
    {
        _shell = shell;
    }

    public bool IsLibrary => _shell.CurrentPage is LibraryViewModel;

    public bool IsChat => _shell.CurrentPage is LibraryChatViewModel;

    public bool IsSettings => _shell.CurrentPage is SettingsViewModel;

    public bool CanAskAi => _shell.CanShowLibraryChat;

    public IReadOnlyList<SettingsSection> Sections => _shell.Settings?.Sections ?? [];

    [RelayCommand]
    private void NewRecording() => _shell.ShowRecording();

    [RelayCommand]
    private void ShowLibrary() => _shell.ShowLibrary();

    [RelayCommand]
    private void ShowChat() => _shell.ShowLibraryChat();

    [RelayCommand]
    private void ShowSettings() => _shell.ShowSettings();

    [RelayCommand]
    private void ShowSection(SettingsSection? section) => _shell.Settings?.ShowSectionCommand.Execute(section);

    /// <summary>The shell moved to another page.</summary>
    internal void Update()
    {
        OnPropertyChanged(nameof(IsLibrary));
        OnPropertyChanged(nameof(IsChat));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(CanAskAi));
        OnPropertyChanged(nameof(Sections));
    }
}
