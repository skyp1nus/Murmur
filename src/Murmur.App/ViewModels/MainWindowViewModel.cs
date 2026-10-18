using CommunityToolkit.Mvvm.ComponentModel;

namespace Murmur.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>Placeholder status line shown until the real shell lands.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "Ready";
}
