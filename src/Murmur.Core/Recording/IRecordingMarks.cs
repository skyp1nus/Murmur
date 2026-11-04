namespace Murmur.Core.Recording;

/// <summary>Marks made during the running recording; shared by the hotkey, the Recording screen and the floating orb (#46).</summary>
public interface IRecordingMarks
{
    /// <summary>Adds a mark at IRecorder.Elapsed when the recorder is Recording or Paused; false (and no mark) otherwise.</summary>
    bool TryMark(out TimeSpan at);

    /// <summary>The marks of the running recording, in order (empty when idle).</summary>
    IReadOnlyList<TimeSpan> Current { get; }

    /// <summary>Raised after a mark is added (on the caller's thread).</summary>
    event Action<TimeSpan>? Marked;
}
