using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Murmur.App.Controls;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

/// <summary>
/// The drop overlay: fades in over the page while files are dragged over the window and, once faded out, leaves the visual tree of
/// the screen (so no automation client sees an empty overlay); under reduced motion there is no fade and it goes at once.
/// </summary>
public partial class DropOverlayView : UserControl
{
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherTimer _hide;
    private DropOverlayViewModel? _viewModel;

    public DropOverlayView()
    {
        InitializeComponent();
        _hide = new DispatcherTimer(DispatcherPriority.Background) { Interval = FadeOut + TimeSpan.FromMilliseconds(30) };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            Root.IsVisible = false;
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as DropOverlayViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
        }

        Apply();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _hide.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == Motion.ReduceMotionProperty)
        {
            Root.Classes.Set("still", change.GetNewValue<bool>());
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DropOverlayViewModel.IsVisible))
        {
            Apply();
        }
    }

    private void Apply()
    {
        if (_viewModel is { IsVisible: true })
        {
            _hide.Stop();
            Root.IsVisible = true;
            Root.Classes.Set("shown", true);
            return;
        }

        Root.Classes.Set("shown", false);
        if (Motion.GetReduceMotion(this) || !Root.IsVisible)
        {
            _hide.Stop();
            Root.IsVisible = false;
        }
        else
        {
            _hide.Stop();
            _hide.Start();
        }
    }
}
