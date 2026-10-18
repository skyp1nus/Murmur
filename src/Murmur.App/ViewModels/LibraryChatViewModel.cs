using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Murmur.App.ViewModels;

/// <summary>
/// "Запитати AI по всіх записах": a page with a library-wide <see cref="ChatViewModel"/> (no scope). The shell keeps it
/// for the whole run, so the conversation survives a visit to a cited session.
/// </summary>
public sealed partial class LibraryChatViewModel : ObservableObject, IDisposable
{
    public LibraryChatViewModel(ChatViewModel chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        Chat = chat;
    }

    /// <summary>Raised when the user wants to go back to the library.</summary>
    public event Action? BackRequested;

    public ChatViewModel Chat { get; }

    public string Title => "Запитати AI по всіх записах";

    public void Dispose() => Chat.Dispose();

    [RelayCommand]
    private void Back() => BackRequested?.Invoke();
}
