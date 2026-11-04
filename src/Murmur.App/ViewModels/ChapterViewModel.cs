using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>One segment of the chapter strip: the title, the start time and a click that seeks there.</summary>
public sealed partial class ChapterViewModel : ObservableObject
{
    private readonly Action<TimeSpan> _seek;
    private RelayCommand? _seekCommand;

    internal ChapterViewModel(Chapter chapter, Action<TimeSpan> seek)
    {
        Title = chapter.Title;
        Start = chapter.Start;
        StartText = UiFormat.Clock(chapter.Start);
        ToolTip = UiText.Format(Strings.Session_ChapterTip, chapter.Title);
        _seek = seek;
    }

    public string Title { get; }

    /// <summary>Where the chapter starts on the session timeline.</summary>
    public TimeSpan Start { get; }

    /// <summary>"12:00", "1:02:03".</summary>
    public string StartText { get; }

    /// <summary>"Go to chapter ..." (the segment jumps there).</summary>
    public string ToolTip { get; }

    /// <summary>The playhead is inside this chapter: the segment is filled.</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>The click: seeks the audio and the video to the start of the chapter.</summary>
    public ICommand SeekCommand => _seekCommand ??= new RelayCommand(() => _seek(Start));
}
