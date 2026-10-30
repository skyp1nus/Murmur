using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Capture;
using Murmur.Core.Transcription;

namespace Murmur.App.ViewModels;

/// <summary>One piece of the line under the title: a text (or the "·" between two) and whether it is set in Geist Mono (the length).</summary>
public sealed record MetaPart(string Text, bool IsMono)
{
    /// <summary>The "·" between two facts.</summary>
    public bool IsSeparator => Text == "·";
}

/// <summary>
/// The player card beyond play and seek: the track filters (You / Others), the speed chip and the facts under the title. The
/// filters silence the track in the player when it can (<see cref="IAudioPlayerControls"/>) and dim the lines of that speaker.
/// </summary>
public sealed partial class SessionViewModel
{
    /// <summary>The speeds the chip cycles through: 1×, 1.25×, 1.5×, 2×, 0.75× and back to 1×.</summary>
    internal static readonly double[] Speeds = [1, 1.25, 1.5, 2, 0.75];

    private readonly IAudioPlayerControls? _controls;
    private int _microphoneTrack = -1;
    private int _systemTrack = -1;
    private int _speedIndex;

    /// <summary>The "You" filter is on: the microphone is heard and its lines are shown at full strength.</summary>
    [ObservableProperty]
    public partial bool YouOn { get; set; } = true;

    /// <summary>The "Others" filter is on (the system audio).</summary>
    [ObservableProperty]
    public partial bool OthersOn { get; set; } = true;

    /// <summary>Playback speed, 1 = real time.</summary>
    [ObservableProperty]
    public partial double Speed { get; private set; } = 1;

    /// <summary>The chip's text: "1×", "1.25×", "0.75×".</summary>
    [ObservableProperty]
    public partial string SpeedText { get; private set; } = SpeedLabel(1);

    /// <summary>The player can silence a track; the filters only dim the lines when it cannot.</summary>
    public bool CanMuteTracks => _controls?.CanMuteTracks == true;

    /// <summary>The player can play at another speed; the chip is disabled when it cannot.</summary>
    public bool CanChangeSpeed => _controls?.CanChangeSpeed == true;

    /// <summary>"1×", "1.25×": the number in the invariant culture, then the multiplication sign.</summary>
    internal static string SpeedLabel(double speed) =>
        speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";

    [RelayCommand(CanExecute = nameof(CanChangeSpeed))]
    private void CycleSpeed()
    {
        _speedIndex = (_speedIndex + 1) % Speeds.Length;
        Speed = Speeds[_speedIndex];
        SpeedText = SpeedLabel(Speed);
        if (_controls is { CanChangeSpeed: true } controls)
        {
            controls.Speed = Speed;
        }
    }

    partial void OnYouOnChanged(bool value) => ApplyTrackFilters();

    partial void OnOthersOnChanged(bool value) => ApplyTrackFilters();

    /// <summary>Called when the audio is open: hands the filters and the speed to the player (they were chosen before it opened).</summary>
    private void ApplyPlayerControls()
    {
        OnPropertyChanged(nameof(CanMuteTracks));
        OnPropertyChanged(nameof(CanChangeSpeed));
        CycleSpeedCommand.NotifyCanExecuteChanged();
        if (_controls is null)
        {
            return;
        }

        if (_controls.CanChangeSpeed)
        {
            _controls.Speed = Speed;
        }

        ApplyTrackFilters();
    }

    /// <summary>Silences the switched-off tracks in the player and dims their lines in the transcript.</summary>
    private void ApplyTrackFilters()
    {
        if (_controls is { CanMuteTracks: true } controls)
        {
            if (_microphoneTrack >= 0)
            {
                controls.SetTrackMuted(_microphoneTrack, !YouOn);
            }

            if (_systemTrack >= 0)
            {
                controls.SetTrackMuted(_systemTrack, !OthersOn);
            }
        }

        foreach (var row in Segments)
        {
            row.IsDimmed = row.Speaker == Speaker.Me ? !YouOn : !OthersOn;
        }
    }

    /// <summary>"Today, 10:30", "47:12", "English", "Parakeet TDT v3" with a "·" between the ones that are known.</summary>
    private List<MetaPart> BuildMeta(Transcript? transcript)
    {
        var parts = new List<MetaPart>(7)
        {
            new(DateText, false),
            new(DurationText, true),
        };
        if (LanguageNames.Of(transcript?.Language) is { Length: > 0 } language)
        {
            parts.Add(new MetaPart(language, false));
        }

        if (EngineNames.Of(transcript?.Engine) is { Length: > 0 } engine)
        {
            parts.Add(new MetaPart(engine, false));
        }

        var result = new List<MetaPart>(parts.Count * 2);
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                result.Add(new MetaPart("·", false));
            }

            result.Add(parts[i]);
        }

        return result;
    }
}

/// <summary>The names of the speech engines in the header: the id the pipeline stores ("parakeet-tdt-0.6b-v3-int8") as a title.</summary>
internal static class EngineNames
{
    public static string? Of(string? engine)
    {
        if (string.IsNullOrWhiteSpace(engine))
        {
            return null;
        }

        var id = engine.Trim();
        if (id.Contains("parakeet", StringComparison.OrdinalIgnoreCase))
        {
            return id.Contains("v3", StringComparison.OrdinalIgnoreCase) ? "Parakeet TDT v3" : "Parakeet TDT";
        }

        return id;
    }
}
