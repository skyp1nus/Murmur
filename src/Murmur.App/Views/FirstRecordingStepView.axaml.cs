using Avalonia.Controls;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

public partial class FirstRecordingStepView : UserControl
{
    public FirstRecordingStepView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // The orb follows the level of the recorder (the same peak the recording screen's orb shows).
        VoiceOrb.LevelSource = DataContext as FirstRecordingStepViewModel;
    }
}
