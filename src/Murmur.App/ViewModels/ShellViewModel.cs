using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>
/// Owns the screens and the navigation between them. The window renders <see cref="CurrentPage"/> through
/// data templates keyed by view-model type (no reflection-based view locator).
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task>? _startup;

    public ShellViewModel(LibraryViewModel library, RecordingViewModel recording, Func<CancellationToken, Task>? startup = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(recording);
        Library = library;
        Recording = recording;
        _startup = startup;
        CurrentPage = library;

        library.NewRecordingRequested += ShowRecording;
        recording.BackRequested += ShowLibrary;
        recording.Stopped += OnRecordingStopped;
    }

    public LibraryViewModel Library { get; }

    public RecordingViewModel Recording { get; }

    /// <summary>Either <see cref="Library"/> or <see cref="Recording"/>.</summary>
    [ObservableProperty]
    public partial ObservableObject CurrentPage { get; set; }

    /// <summary>Builds the screens from the services handed over by the composition root.</summary>
    public static ShellViewModel Create(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new ShellViewModel(
            new LibraryViewModel(services.Sessions, services.Time),
            new RecordingViewModel(services.Recorder, services.Capture, services.Sessions),
            services.Startup);
    }

    /// <summary>
    /// Runs the startup work (crash recovery) off the UI thread, then loads the library. A startup failure is shown
    /// in the library instead of crashing the app.
    /// </summary>
    public async Task InitializeAsync()
    {
        string? startupError = null;
        if (_startup is { } startup)
        {
            try
            {
                await Task.Run(() => startup(CancellationToken.None));
            }
            catch (Exception ex)
            {
                startupError = "Не вдалося відновити перервані записи: " + ex.Message;
            }
        }

        await Library.LoadAsync();
        if (startupError is not null && Library.Error is null)
        {
            Library.Error = startupError;
        }
    }

    public void ShowRecording()
    {
        Recording.Prepare();
        CurrentPage = Recording;
    }

    public void ShowLibrary()
    {
        CurrentPage = Library;
        _ = Library.LoadAsync();
    }

    private void OnRecordingStopped(SessionRecord session) => ShowLibrary();
}
