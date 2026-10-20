using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Murmur.App.ViewModels;

/// <summary>
/// The 60 px bar above every main screen: <see cref="Leading"/> on the left (the breadcrumb by default), <see cref="Trailing"/> on the
/// right (the "Record Ctrl Shift R" hint by default, the screen's own buttons on the recording view). Both are plain view models
/// the window shows through data templates, so a screen puts its own header content there without touching the frame:
/// the session screen sets <see cref="Trailing"/> to its Export and "…" buttons, the chat its scope pills and model chip.
/// </summary>
public sealed partial class TopBarViewModel : ObservableObject
{
    /// <summary>Left side; <c>null</c> = nothing.</summary>
    [ObservableProperty]
    public partial object? Leading { get; set; }

    /// <summary>Right side; <c>null</c> = nothing.</summary>
    [ObservableProperty]
    public partial object? Trailing { get; set; }
}

/// <summary>"Library / New recording": the first part is a link back when <see cref="RootCommand"/> is set, the rest is the current place.</summary>
public sealed class BreadcrumbViewModel
{
    public BreadcrumbViewModel(string root, ICommand? rootCommand = null, string? current = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = root;
        RootCommand = rootCommand;
        Current = current;
    }

    public string Root { get; }

    /// <summary>The place below the root ("New recording", the title of the open session); <c>null</c> on a top-level screen.</summary>
    public string? Current { get; }

    public bool HasCurrent => Current is not null;

    /// <summary>Goes back to the root; <c>null</c> = the root is plain text.</summary>
    public ICommand? RootCommand { get; }

    /// <summary>The root is a link (it has a command and something below it).</summary>
    public bool IsLink => RootCommand is not null && Current is not null;

    /// <summary>The root is the whole breadcrumb: dark grey text instead of the muted link colour.</summary>
    public bool IsPlain => Current is null;
}

/// <summary>"Record Ctrl Shift R" / "Stop Ctrl Shift R": what the record hotkey does now, and its gesture from the settings.</summary>
public sealed partial class HotkeyHintViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    /// <summary>The gesture as key caps ("Ctrl Shift R"); empty = no hotkey set, the hint is hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGesture))]
    public partial string Gesture { get; set; } = string.Empty;

    public bool HasGesture => Gesture.Length > 0;
}
