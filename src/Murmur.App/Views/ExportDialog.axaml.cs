using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

/// <summary>
/// The export dialog: a scrim and the sheet in the middle. It is not a window: <see cref="Show"/> puts it on the overlay layer of the window
/// the opener is in, over everything, so the scrim dims the app as in the mockup. Esc, the cross and a click on the scrim close it.
/// </summary>
public partial class ExportDialog : UserControl
{
    private OverlayLayer? _layer;
    private TopLevel? _top;
    private IInputElement? _previousFocus;
    private ExportDialogViewModel? _viewModel;

    public ExportDialog()
    {
        InitializeComponent();
        // A modal with keys of its own: the session shortcuts (#61) stay out of it.
        SessionShortcuts.SetOwnsKeys(this, true);
        Scrim.PointerPressed += OnScrimPressed;
    }

    /// <summary>The dialog is on the overlay layer.</summary>
    public bool IsOpen => _layer is not null;

    /// <summary>
    /// Shows the dialog for <paramref name="viewModel"/> over the window that <paramref name="anchor"/> is in and gives it the focus.
    /// Returns <c>null</c> when the anchor is in no window.
    /// </summary>
    public static ExportDialog? Show(Visual anchor, ExportDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(viewModel);
        if (TopLevel.GetTopLevel(anchor) is not { } top || OverlayLayer.GetOverlayLayer(anchor) is not { } layer)
        {
            return null;
        }

        if (top.Clipboard is { } clipboard)
        {
            viewModel.Clipboard = text => clipboard.SetTextAsync(text);
        }

        var dialog = new ExportDialog { DataContext = viewModel };
        dialog.Open(top, layer, viewModel, top.FocusManager?.GetFocusedElement());
        return dialog;
    }

    /// <summary>Takes the dialog off the overlay layer, gives the focus back and lets the view model go.</summary>
    public void Close()
    {
        if (_layer is not { } layer)
        {
            return;
        }

        _layer = null;
        _top?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        _top = null;
        layer.PropertyChanged -= OnLayerPropertyChanged;
        layer.Children.Remove(this);
        if (_viewModel is { } viewModel)
        {
            viewModel.CloseRequested -= Close;
            viewModel.Dispose();
            _viewModel = null;
        }

        _previousFocus?.Focus();
        _previousFocus = null;
    }

    private void Open(TopLevel top, OverlayLayer layer, ExportDialogViewModel viewModel, IInputElement? previousFocus)
    {
        _top = top;
        _layer = layer;
        _viewModel = viewModel;
        _previousFocus = previousFocus;
        viewModel.CloseRequested += Close;
        layer.PropertyChanged += OnLayerPropertyChanged;
        Width = layer.Bounds.Width;
        Height = layer.Bounds.Height;
        // Esc closes the dialog wherever the focus is (the window's own handlers never see it).
        top.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        layer.Children.Add(this);

        // The format that is chosen takes the focus (once the dialog is laid out): Tab walks through the dialog.
        FormatMarkdown.Focus();
        Dispatcher.UIThread.Post(() => FormatMarkdown.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>The scrim follows the size of the window.</summary>
    private void OnLayerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty && sender is OverlayLayer layer)
        {
            Width = layer.Bounds.Width;
            Height = layer.Bounds.Height;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled && _viewModel is { } viewModel)
        {
            viewModel.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>A click outside the sheet closes the dialog (a click inside is for the controls and never gets here).</summary>
    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, Scrim) && _viewModel is { } viewModel)
        {
            viewModel.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }
}
