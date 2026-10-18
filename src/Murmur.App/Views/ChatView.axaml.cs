using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

/// <summary>The "Запитати AI" chat: conversation list, input pill, Ask / Stop. Used by the recording view and the library-wide chat.</summary>
public partial class ChatView : UserControl
{
    private ChatViewModel? _viewModel;
    private bool _scrollQueued;

    public ChatView()
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
        }

        _viewModel = DataContext as ChatViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested += OnScrollToEnd;
        }
    }

    /// <summary>Enter asks (Shift+Enter is left to the text box).</summary>
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
                Scroller.ScrollToEnd();
            },
            DispatcherPriority.Render);
    }
}
