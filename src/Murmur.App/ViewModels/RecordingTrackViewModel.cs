using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.App.Controls;
using Murmur.App.Resources;

namespace Murmur.App.ViewModels;

/// <summary>What a track of the Tracks card is doing.</summary>
public enum RecordingTrackState
{
    /// <summary>Recording: the level in dB, the bars and the device name.</summary>
    Active,

    /// <summary>The recording is paused: nothing is written, the bars stand still.</summary>
    Paused,

    /// <summary>Not part of this recording (the user turned the source off).</summary>
    Off,

    /// <summary>Windows privacy settings deny the device.</summary>
    Denied,

    /// <summary>The device is missing or could not be started.</summary>
    Unavailable,
}

/// <summary>
/// One row of the Tracks card on the Recording screen: who it is (icon and name), the level in dB, the strip of recent levels
/// and the device name. A track that is off, denied or unavailable keeps the same layout and says so instead of the level.
/// </summary>
public sealed partial class RecordingTrackViewModel : ObservableObject
{
    private double _lastDb = LevelHistory.FloorDb;
    private int _shownDb = int.MinValue;
    private string? _shownDbFormat;
    private string _shownDbText = string.Empty;

    public RecordingTrackViewModel(bool isMicrophone)
    {
        IsMicrophone = isMicrophone;
        Label = isMicrophone ? Strings.Recording_TrackYou : Strings.Recording_TrackOthers;
    }

    /// <summary>The microphone ("You"), otherwise the system audio ("Others").</summary>
    public bool IsMicrophone { get; }

    public bool IsSystemAudio => !IsMicrophone;

    /// <summary>"You · microphone" / "Others · system audio".</summary>
    public string Label { get; }

    /// <summary>The recent levels, drawn as bars.</summary>
    public LevelHistory History { get; } = new();

    [ObservableProperty]
    public partial RecordingTrackState State { get; private set; } = RecordingTrackState.Off;

    /// <summary>The level (<c>−18 dB</c>), or the word for a track that is not recording.</summary>
    [ObservableProperty]
    public partial string LevelText { get; private set; } = string.Empty;

    /// <summary>The device name (system audio: with "loopback"), or why there is no level.</summary>
    [ObservableProperty]
    public partial string Detail { get; private set; } = string.Empty;

    /// <summary>The bars show nothing but stubs: the track is not recording.</summary>
    public bool IsIdle => State is RecordingTrackState.Off or RecordingTrackState.Denied or RecordingTrackState.Unavailable;

    /// <summary>The last level shown, in dB (−60 for silence); for tests.</summary>
    internal double LastDb => _lastDb;

    /// <summary>Back to the empty state when a recording ends.</summary>
    internal void Reset()
    {
        History.Clear();
        _lastDb = LevelHistory.FloorDb;
        _shownDb = int.MinValue;
        State = RecordingTrackState.Off;
        LevelText = string.Empty;
        Detail = string.Empty;
    }

    /// <summary>Takes the current state of the track; a new level sample is added to the strip only while <paramref name="push"/>.</summary>
    internal void Apply(RecordingTrackState state, float peak, string detail, bool push)
    {
        State = state;
        Detail = detail;
        if (state == RecordingTrackState.Active)
        {
            if (push)
            {
                History.Push(peak);
            }

            _lastDb = LevelHistory.DecibelsOf(peak);
            LevelText = LevelDbText(_lastDb);
        }
        else
        {
            LevelText = state switch
            {
                RecordingTrackState.Paused => Strings.Recording_TrackPaused,
                RecordingTrackState.Denied => Strings.Recording_TrackDenied,
                RecordingTrackState.Unavailable => Strings.Recording_TrackLost,
                _ => Strings.Recording_TrackOff,
            };
        }
    }

    /// <summary>
    /// "−18 dB" for a level. The text only has a whole number in it, so the last one is kept and built again only when the
    /// number (or the language, which changes the format string) changes: a steady level costs nothing per poll.
    /// </summary>
    private string LevelDbText(double db)
    {
        var rounded = (int)Math.Round(db, MidpointRounding.AwayFromZero);
        var format = Strings.Recording_LevelDb;
        if (rounded != _shownDb || !ReferenceEquals(format, _shownDbFormat))
        {
            _shownDb = rounded;
            _shownDbFormat = format;
            _shownDbText = UiText.Format(format, FormatDb(db));
        }

        return _shownDbText;
    }

    /// <summary>A whole number with the typographic minus of the canvas ("−18"); 0 stays "0".</summary>
    internal static string FormatDb(double db)
    {
        var rounded = (int)Math.Round(db, MidpointRounding.AwayFromZero);
        return rounded < 0 ? "−" + (-rounded).ToString(System.Globalization.CultureInfo.InvariantCulture) : rounded.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    partial void OnStateChanged(RecordingTrackState value) => OnPropertyChanged(nameof(IsIdle));
}
