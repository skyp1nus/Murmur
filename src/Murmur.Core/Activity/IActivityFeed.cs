namespace Murmur.Core.Activity;

public enum ActivityKind
{
    Recording,
    Transcribing,
    Indexing,
    Summarizing,
    DownloadingModel,
}

/// <summary>One thing that runs or waits right now.</summary>
/// <param name="Key">Stable per item (e.g. <c>rec</c>, <c>asr:&lt;sessionId&gt;</c>, <c>model:&lt;id&gt;</c>) so the UI can diff rows.</param>
/// <param name="Title">Session title or model name (not localised; the App words the kind).</param>
/// <param name="Progress">0..1, or <c>null</c> when queued / indeterminate.</param>
public sealed record ActivityItem(string Key, ActivityKind Kind, string Title, double? Progress, bool Queued);

/// <summary>Facts only: what runs or waits right now. Thread-safe; <see cref="Changed"/> is raised off the UI thread, coalesced (at most 4 Hz).</summary>
public interface IActivityFeed
{
    IReadOnlyList<ActivityItem> Current { get; }

    event Action? Changed;
}
