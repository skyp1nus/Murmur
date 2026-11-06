using Avalonia.Controls;

namespace Murmur.App.Views;

/// <summary>The row of keyboard shortcuts under the recording (#61). Purely a legend: the keys are handled by <see cref="SessionShortcuts"/>.</summary>
public partial class ShortcutsBar : UserControl
{
    public ShortcutsBar() => InitializeComponent();
}
