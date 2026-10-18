using Murmur.Core.Sessions;

namespace Murmur.App.Tests.Fakes;

internal static class Sessions
{
    public static SessionRecord Make(
        string title,
        DateTimeOffset startedAt,
        TimeSpan duration,
        bool mic = true,
        bool system = true,
        bool screen = false,
        SessionState state = SessionState.Recorded) =>
        new()
        {
            Id = SessionId.New(),
            Title = title,
            StartedAt = startedAt,
            Duration = duration,
            State = state,
            HasMicrophone = mic,
            HasSystemAudio = system,
            ScreenSegments = screen ? [new ScreenSegment(0, TimeSpan.Zero, duration)] : [],
        };
}
