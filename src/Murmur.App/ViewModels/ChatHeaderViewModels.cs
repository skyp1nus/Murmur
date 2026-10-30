using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Intelligence;
using Murmur.Core.Settings;

namespace Murmur.App.ViewModels;

/// <summary>
/// The pills at the left of the Ask AI header: "Whole library" and, when the chat was opened from a recording or a saved thread is
/// scoped to one, the recording's title. The dot marks the active scope. The scope belongs to the thread: picking the other pill
/// starts a new chat in that scope (the chat in view is kept in the list).
/// </summary>
public sealed partial class ChatScopeViewModel : ObservableObject
{
    private readonly Action<bool> _pick;

    internal ChatScopeViewModel(Action<bool> pick)
    {
        _pick = pick;
    }

    public string LibraryText => Strings.Chat_ScopeLibrary;

    /// <summary>The recording's title; empty when there is no recording pill.</summary>
    [ObservableProperty]
    public partial string SessionTitle { get; private set; } = string.Empty;

    /// <summary>The recording pill is shown (the chat was opened from a recording, or the thread is scoped to one).</summary>
    [ObservableProperty]
    public partial bool HasSession { get; private set; }

    /// <summary>The questions are asked over the whole library.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSession))]
    public partial bool IsLibrary { get; private set; } = true;

    /// <summary>The questions are asked about the recording of the second pill.</summary>
    public bool IsSession => HasSession && !IsLibrary;

    internal void Show(string? sessionTitle, bool sessionScope)
    {
        HasSession = sessionTitle is not null;
        SessionTitle = sessionTitle ?? string.Empty;
        IsLibrary = !sessionScope;
        OnPropertyChanged(nameof(IsSession));
    }

    [RelayCommand]
    private void PickLibrary() => _pick(false);

    [RelayCommand]
    private void PickSession() => _pick(true);
}

/// <summary>
/// The model chip at the right of the Ask AI header: "Gemma 4 E4B · local", and the acceleration when it is not the CPU one
/// ("Gemma 4 E4B · local · Vulkan"; D-042). Follows the acceleration status while the app runs (a GPU server that failed to start
/// falls back to the CPU build and the chip drops the suffix).
/// </summary>
public sealed partial class ChatModelChipViewModel : ObservableObject, IDisposable
{
    private readonly Func<string?> _modelName;
    private readonly IAccelerationStatusSource? _acceleration;
    private readonly Action<Action> _post;

    /// <param name="modelName">The name of the active chat model (read again when the chip refreshes); <c>null</c> or blank = a generic name.</param>
    /// <param name="acceleration">Which build runs; <c>null</c> = always the CPU one.</param>
    /// <param name="post">Marshals the status change to the UI thread; the dispatcher by default.</param>
    public ChatModelChipViewModel(Func<string?> modelName, IAccelerationStatusSource? acceleration = null, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        _modelName = modelName;
        _acceleration = acceleration;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        if (acceleration is not null)
        {
            acceleration.Changed += OnAccelerationChanged;
        }
    }

    private string? _text;

    /// <summary>"Gemma 4 E4B · local"; read when the header first shows it (the model catalog is not asked at start-up).</summary>
    public string Text => _text ??= Build();

    /// <summary>Reads the model name and the acceleration again.</summary>
    public void Refresh()
    {
        _text = Build();
        OnPropertyChanged(nameof(Text));
    }

    public void Dispose()
    {
        if (_acceleration is not null)
        {
            _acceleration.Changed -= OnAccelerationChanged;
        }
    }

    private void OnAccelerationChanged(AccelerationStatus status) => _post(Refresh);

    private string Build()
    {
        var name = _modelName();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Strings.Chat_ModelUnknown;
        }

        return _acceleration?.Current.Active switch
        {
            AiAcceleration.Vulkan => UiText.Format(Strings.Chat_ModelLocalAccelerated, name, "Vulkan"),
            AiAcceleration.Cuda => UiText.Format(Strings.Chat_ModelLocalAccelerated, name, "CUDA"),
            _ => UiText.Format(Strings.Chat_ModelLocal, name),
        };
    }
}
