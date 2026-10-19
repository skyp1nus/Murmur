using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.App.Resources;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>
/// One row of the library list. Everything is fixed for the row's lifetime except the status badge, which follows the
/// processing pipeline (see <see cref="ApplyProcessing"/>), and the stored facts, which <see cref="Update"/> re-reads when
/// the session changed behind the library's back (a background recovery); a reload builds new rows.
/// </summary>
public sealed partial class SessionItemViewModel : ObservableObject
{
    public SessionItemViewModel(SessionRecord record, TimeProvider time, SessionProcessingStatus? processing = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(time);
        Id = record.Id;
        Record = record;
        Title = TitleOf(record);
        DateText = UiFormat.Date(record.StartedAt, time);
        DurationText = UiFormat.Duration(record.Duration);
        HasMicrophone = record.HasMicrophone;
        HasSystemAudio = record.HasSystemAudio;
        HasScreen = record.ScreenSegments.Count > 0;
        StatusText = ProcessingText.Badge(record.State, processing);
    }

    /// <summary>The stored session this row shows (handed to the recording view when the row is opened).</summary>
    public SessionRecord Record { get; private set; }

    public SessionId Id { get; }

    [ObservableProperty]
    public partial string Title { get; private set; }

    [ObservableProperty]
    public partial string DateText { get; private set; }

    /// <summary>Shown in Geist Mono.</summary>
    [ObservableProperty]
    public partial string DurationText { get; private set; }

    [ObservableProperty]
    public partial bool HasMicrophone { get; private set; }

    [ObservableProperty]
    public partial bool HasSystemAudio { get; private set; }

    [ObservableProperty]
    public partial bool HasScreen { get; private set; }

    /// <summary>Processing / outcome badge ("Транскрибується 42 %", "Готово", "Помилка", "Перервано"); <c>null</c> = none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? StatusText { get; private set; }

    public bool HasStatus => StatusText is not null;

    /// <summary>Updates the badge from a pipeline status event. Equal text raises no notification.</summary>
    public void ApplyProcessing(SessionProcessingStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        StatusText = ProcessingText.Badge(Record.State, status);
    }

    /// <summary>Shows a newer stored version of the same session in place (the row object, and so the list, stays).</summary>
    internal void Update(SessionRecord record, TimeProvider time, SessionProcessingStatus? processing)
    {
        Record = record;
        Title = TitleOf(record);
        DateText = UiFormat.Date(record.StartedAt, time);
        DurationText = UiFormat.Duration(record.Duration);
        HasMicrophone = record.HasMicrophone;
        HasSystemAudio = record.HasSystemAudio;
        HasScreen = record.ScreenSegments.Count > 0;
        StatusText = ProcessingText.Badge(record.State, processing);
    }

    private static string TitleOf(SessionRecord record) =>
        string.IsNullOrWhiteSpace(record.Title) ? Strings.Common_UntitledRecording : record.Title;
}
