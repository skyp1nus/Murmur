using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>One row of the library list. Immutable: a reload builds new rows.</summary>
public sealed class SessionItemViewModel
{
    public SessionItemViewModel(SessionRecord record, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(time);
        Id = record.Id;
        Title = string.IsNullOrWhiteSpace(record.Title) ? "Запис без назви" : record.Title;
        DateText = UiFormat.Date(record.StartedAt, time);
        DurationText = UiFormat.Duration(record.Duration);
        HasMicrophone = record.HasMicrophone;
        HasSystemAudio = record.HasSystemAudio;
        HasScreen = record.ScreenSegments.Count > 0;
        StatusText = record.State switch
        {
            SessionState.Interrupted => "Перервано",
            SessionState.Failed => "Помилка",
            SessionState.Recording => "Триває запис",
            _ => null,
        };
    }

    public SessionId Id { get; }

    public string Title { get; }

    public string DateText { get; }

    /// <summary>Shown in Geist Mono.</summary>
    public string DurationText { get; }

    public bool HasMicrophone { get; }

    public bool HasSystemAudio { get; }

    public bool HasScreen { get; }

    /// <summary>Only for sessions that did not finish normally; <c>null</c> otherwise.</summary>
    public string? StatusText { get; }

    public bool HasStatus => StatusText is not null;
}
