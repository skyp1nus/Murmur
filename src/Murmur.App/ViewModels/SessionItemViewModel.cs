using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>
/// One row of the library list. Everything is fixed for the row's lifetime except the status badge, which follows the
/// processing pipeline (see <see cref="ApplyProcessing"/>); a reload builds new rows.
/// </summary>
public sealed partial class SessionItemViewModel : ObservableObject
{
    public SessionItemViewModel(SessionRecord record, TimeProvider time, SessionProcessingStatus? processing = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(time);
        Record = record;
        Id = record.Id;
        Title = string.IsNullOrWhiteSpace(record.Title) ? "Запис без назви" : record.Title;
        DateText = UiFormat.Date(record.StartedAt, time);
        DurationText = UiFormat.Duration(record.Duration);
        HasMicrophone = record.HasMicrophone;
        HasSystemAudio = record.HasSystemAudio;
        HasScreen = record.ScreenSegments.Count > 0;
        StatusText = ProcessingText.Badge(record.State, processing);
    }

    /// <summary>The stored session this row shows (handed to the recording view when the row is opened).</summary>
    public SessionRecord Record { get; }

    public SessionId Id { get; }

    public string Title { get; }

    public string DateText { get; }

    /// <summary>Shown in Geist Mono.</summary>
    public string DurationText { get; }

    public bool HasMicrophone { get; }

    public bool HasSystemAudio { get; }

    public bool HasScreen { get; }

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
}
