using System.Globalization;
using Murmur.App.Resources;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>User-visible wording of the post-recording state, shared by the library rows and the recording view.</summary>
internal static class ProcessingText
{
    /// <summary>Whole percent in [0, 99] while the work is not finished (100 only on <see cref="SessionProcessingStage.Completed"/>).</summary>
    public static int Percent(double progress) => (int)Math.Clamp(Math.Floor(progress * 100), 0, 99);

    /// <summary>"42 %".</summary>
    public static string PercentText(double progress) => UiText.Format(Strings.Common_Percent, Percent(progress));

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
                    return Strings.Processing_Queued;
                case SessionProcessingStage.BuildingPeaks:
                case SessionProcessingStage.Transcribing:
                case SessionProcessingStage.Saving:
                    return UiText.Format(Strings.Processing_TranscribingPercent, PercentText(status.Progress));
                case SessionProcessingStage.Indexing:
                    return Strings.Processing_Indexing;
                case SessionProcessingStage.Completed:
                    return Strings.Processing_Done;
                case SessionProcessingStage.Failed:
                    return Strings.Processing_Failed;
                case SessionProcessingStage.ModelMissing:
                    return Strings.Processing_NoTranscript;
            }
        }

        return state switch
        {
            SessionState.Interrupted => Strings.Processing_Interrupted,
            SessionState.Failed => Strings.Processing_Failed,
            SessionState.Recording => Strings.Processing_Recording,
            SessionState.Transcribing => Strings.Processing_Transcribing,
            SessionState.Transcribed or SessionState.Indexing => Strings.Processing_Indexing,
            SessionState.Ready => Strings.Processing_Done,
            _ => null,
        };
    }

    /// <summary>The stage as a sentence for the recording view's progress card.</summary>
    public static string Stage(SessionProcessingStage stage) => stage switch
    {
        SessionProcessingStage.Queued => Strings.Stage_Queued,
        SessionProcessingStage.BuildingPeaks => Strings.Stage_BuildingPeaks,
        SessionProcessingStage.Transcribing => Strings.Stage_Transcribing,
        SessionProcessingStage.Saving => Strings.Stage_Saving,
        SessionProcessingStage.Indexing => Strings.Stage_Indexing,
        _ => Strings.Stage_Default,
    };
}
