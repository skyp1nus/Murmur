using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Murmur.App.ViewModels;

/// <summary>Phase 0 placeholder of the Library screen: header, search box and the empty state.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>Text typed into the library search box.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Sample timer shown under the empty state (the real timer arrives with the recording screen).</summary>
    [ObservableProperty]
    public partial string TimerSample { get; set; } = "00:00:00";

    [RelayCommand]
    private void StartRecording()
    {
        // Wired to IRecordingSession in the recording phase.
    }

    [RelayCommand]
    private void OpenSettings()
    {
        // Settings screen arrives in a later phase.
    }
}
