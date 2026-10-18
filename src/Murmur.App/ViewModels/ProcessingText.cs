using System.Globalization;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>User-visible wording of the post-recording state, shared by the library rows and the recording view.</summary>
internal static class ProcessingText
{
    /// <summary>Whole percent in [0, 99] while the work is not finished (100 only on <see cref="SessionProcessingStage.Completed"/>).</summary>
    public static int Percent(double progress) => (int)Math.Clamp(Math.Floor(progress * 100), 0, 99);

    /// <summary>"42 %".</summary>
    public static string PercentText(double progress) => string.Create(CultureInfo.InvariantCulture, $"{Percent(progress)} %");

    /// <summary>True while the pipeline is working on (or waiting to work on) the session.</summary>
    public static bool IsBusy(SessionProcessingStage stage) =>
        stage is SessionProcessingStage.Queued or SessionProcessingStage.BuildingPeaks or SessionProcessingStage.Transcribing
            or SessionProcessingStage.Saving or SessionProcessingStage.Indexing;

    /// <summary>
    /// The badge of a library row: the live pipeline status wins over the stored state. <c>null</c> = no badge
    /// (a plain finished recording that nobody has touched).
    /// </summary>
    public static string? Badge(SessionState state, SessionProcessingStatus? live)
    {
        if (live is { } status)
        {
            switch (status.Stage)
            {
                case SessionProcessingStage.Queued:
                    return "У черзі";
                case SessionProcessingStage.BuildingPeaks:
                case SessionProcessingStage.Transcribing:
                case SessionProcessingStage.Saving:
                    return "Транскрибується " + PercentText(status.Progress);
                case SessionProcessingStage.Indexing:
                    return "Індексується";
                case SessionProcessingStage.Completed:
                    return "Готово";
                case SessionProcessingStage.Failed:
                    return "Помилка";
                case SessionProcessingStage.ModelMissing:
                    return "Без транскрипту";
            }
        }

        return state switch
        {
            SessionState.Interrupted => "Перервано",
            SessionState.Failed => "Помилка",
            SessionState.Recording => "Триває запис",
            SessionState.Transcribing => "Транскрибується",
            SessionState.Transcribed or SessionState.Indexing => "Індексується",
            SessionState.Ready => "Готово",
            _ => null,
        };
    }

    /// <summary>The stage as a sentence for the recording view's progress card.</summary>
    public static string Stage(SessionProcessingStage stage) => stage switch
    {
        SessionProcessingStage.Queued => "У черзі на транскрибування",
        SessionProcessingStage.BuildingPeaks => "Готуємо звукову доріжку",
        SessionProcessingStage.Transcribing => "Транскрибуємо мовлення",
        SessionProcessingStage.Saving => "Зберігаємо транскрипт",
        SessionProcessingStage.Indexing => "Індексуємо для пошуку",
        _ => "Обробка",
    };
}
