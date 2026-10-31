using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

/// <summary>The conversation of Ask AI: the virtualised messages, the composer with Send / Stop, Copy answer on the clipboard.</summary>
public partial class ConversationView : UserControl
{
    private ChatViewModel? _viewModel;
    private bool _scrollQueued;
    private ScrollViewer? _scroller;

    public ConversationView()
    {
        InitializeComponent();

        // Tunnel: the text box would otherwise see Enter first.
        QuestionBox.AddHandler(KeyDownEvent, OnQuestionKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested -= OnScrollToEnd;
            _viewModel.ClipboardWriter = null;
        }

        _viewModel = DataContext as ChatViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested += OnScrollToEnd;
            _viewModel.ClipboardWriter = WriteToClipboardAsync;
        }
    }

    /// <summary>Enter asks (Shift+Enter is a new line).</summary>
    private void OnQuestionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || _viewModel is null)
        {
            return;
        }

        if (_viewModel.AskCommand.CanExecute(null))
        {
            _viewModel.AskCommand.Execute(null);
        }

        e.Handled = true;
    }

    /// <summary>The window's clipboard; the view model catches what it throws (the Windows clipboard fails while another program holds it).</summary>
    private async Task<bool> WriteToClipboardAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return false;
        }

        await clipboard.SetTextAsync(text);
        return true;
    }

    /// <summary>Deltas arrive many times a second: one scroll per rendered frame at most, after layout.</summary>
    private void OnScrollToEnd()
    {
        if (_scrollQueued)
        {
            return;
        }

        _scrollQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _scrollQueued = false;
                (_scroller ??= MessageList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault())?.ScrollToEnd();
            },
            DispatcherPriority.Render);
    }
}
