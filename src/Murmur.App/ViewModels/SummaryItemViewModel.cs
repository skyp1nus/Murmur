using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.Core.Intelligence;

namespace Murmur.App.ViewModels;

/// <summary>One key point or action item of the summary: the text and, when it cites a moment, a chip that plays from there.</summary>
public sealed partial class SummaryItemViewModel : ObservableObject
{
    private readonly Action<TimeSpan> _play;
    private RelayCommand? _playCommand;

    internal SummaryItemViewModel(SummaryItem item, Action<TimeSpan> play)
    {
        Text = item.Text;
        At = item.At;
        _play = play;
    }

    public string Text { get; }

    public TimeSpan? At { get; }

    /// <summary>The time chip is shown only for an item that cites a moment.</summary>
    public bool HasTime => At is not null;

    /// <summary>"12:52"; empty without a moment.</summary>
    public string TimeText => At is { } at ? UiFormat.Clock(at) : string.Empty;

    /// <summary>The checkbox of an action item (a note for the user; not stored).</summary>
    [ObservableProperty]
    public partial bool IsDone { get; set; }

    /// <summary>The chip: seeks the player to the moment and plays.</summary>
    public ICommand PlayCommand => _playCommand ??= new RelayCommand(() =>
    {
        if (At is { } at)
        {
            _play(at);
        }
    });
}
