namespace Murmur.Core.Sessions;

public readonly record struct SessionId(Guid Value)
{
    /// <summary>Creates a time-ordered id (UUIDv7), so ids sort by creation time.</summary>
    public static SessionId New() => new(Guid.CreateVersion7());

    public static SessionId Parse(string value) => new(Guid.ParseExact(value, "N"));

    public override string ToString() => Value.ToString("N");
}

public enum SessionState
{
    Recording,
    Recorded,
    Transcribing,
    Transcribed,
    Indexing,
    Ready,
    Failed,

    /// <summary>The app died while recording; flushed audio was recovered on the next start.</summary>
    Interrupted,
}

/// <summary>One continuous piece of screen video, positioned on the session timeline.</summary>
/// <param name="Offset">Session time of media time 0 of the segment file (the audio tracks start at session time 0).</param>
/// <param name="Duration">Session time the segment covers; equals the media duration of the file.</param>
public sealed record ScreenSegment(int Index, TimeSpan Offset, TimeSpan Duration);

public sealed record SessionRecord
{
    public required SessionId Id { get; init; }

    public required string Title { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public TimeSpan Duration { get; init; }

    public SessionState State { get; init; }

    public bool HasMicrophone { get; init; }

    public bool HasSystemAudio { get; init; }

    public IReadOnlyList<ScreenSegment> ScreenSegments { get; init; } = [];

    /// <summary>Last processing error, if <see cref="State"/> is <see cref="SessionState.Failed"/>.</summary>
    public string? Error { get; init; }
}

/// <summary>File layout of a session folder. Only <c>Murmur.Storage</c> decides where the folder is.</summary>
public sealed record SessionFiles(string Directory)
{
    public string MicrophoneAudio => Path.Combine(Directory, "mic.wav");

    public string SystemAudio => Path.Combine(Directory, "system.wav");

    public string Peaks => Path.Combine(Directory, "peaks.bin");

    public string ScreenSegment(int index) => Path.Combine(Directory, $"screen-{index:D3}.mp4");
}

public sealed record SessionQuery(DateTimeOffset? From = null, DateTimeOffset? To = null, int Limit = 500);
