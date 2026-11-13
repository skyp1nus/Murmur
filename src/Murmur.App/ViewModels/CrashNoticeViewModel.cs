using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Services;
using Murmur.Core.Diagnostics;

namespace Murmur.App.ViewModels;

/// <summary>
/// The quiet line above the Library after a crash (#100): "Murmur closed unexpectedly last time." with "Open log" and "Dismiss". Shown while
/// <see cref="IAppLog.UnseenCrash"/> is set at start; dismissing it marks the crash as seen, so the next start does not show it again. The
/// null log of the designer, the tests and the parity boards never has a crash, so the line never shows there.
/// </summary>
public sealed partial class CrashNoticeViewModel : ObservableObject
{
    private readonly IAppLog _log;
    private readonly IFolderLauncher _launcher;
    private readonly string? _crashFile;

    /// <param name="log">The diagnostic log; <c>null</c> = <see cref="NullAppLog"/>.</param>
    /// <param name="launcher">Opens the crash file (or the logs folder when the file cannot be opened); the OS shell by default.</param>
    public CrashNoticeViewModel(IAppLog? log = null, IFolderLauncher? launcher = null)
    {
        _log = log ?? NullAppLog.Instance;
        _launcher = launcher ?? SystemFolderLauncher.Instance;
        _crashFile = _log.UnseenCrash;
        IsVisible = _crashFile is not null;
    }

    /// <summary>The previous run crashed and the user has not dismissed the line yet.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; private set; }

    /// <summary>Opens the crash file; when that fails (the file is gone, no app for .txt), the logs folder instead.</summary>
    [RelayCommand]
    private void OpenLog()
    {
        if (_crashFile is not null && _launcher.TryOpen(_crashFile) is null)
        {
            return;
        }

        if (_log.Directory.Length > 0)
        {
            _ = _launcher.TryOpen(_log.Directory);
        }
    }

    [RelayCommand]
    private void Dismiss()
    {
        IsVisible = false;
        _log.MarkCrashSeen();
    }
}
