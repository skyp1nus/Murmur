using Avalonia.Controls;
using Avalonia.Interactivity;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

public partial class OnboardingView : UserControl
{
    public OnboardingView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The page leaves the window (another page replaces it, the window closes): the audio meters and the trial recording
    /// stop. The shell does the same when it changes the page; this covers a view that is torn down on its own.
    /// </summary>
    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        (DataContext as OnboardingViewModel)?.Suspend();
    }
}
