using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests.Fakes;

/// <summary>An in-memory <see cref="ISpeakerNameStore"/> that trims and cuts like the real one and records every write.</summary>
internal sealed class FakeSpeakerNameStore : ISpeakerNameStore
{
    private readonly Dictionary<SessionId, SpeakerNames> _names = [];

    public List<(SessionId Session, Speaker Speaker, string? Name)> Writes { get; } = [];

    public Exception? SetFailure { get; set; }

    public Exception? GetFailure { get; set; }

    public void Put(SessionId session, SpeakerNames names) => _names[session] = names;

    public ValueTask<SpeakerNames> GetSpeakerNamesAsync(SessionId session, CancellationToken cancellationToken) =>
        GetFailure is { } failure ? throw failure : ValueTask.FromResult(_names.GetValueOrDefault(session) ?? SpeakerNames.Default);

    public ValueTask SetSpeakerNameAsync(SessionId session, Speaker speaker, string? name, CancellationToken cancellationToken)
    {
        if (SetFailure is { } failure)
        {
            throw failure;
        }

        Writes.Add((session, speaker, name));
        var text = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (text is { Length: > 40 })
        {
            text = text[..40];
        }

        var current = _names.GetValueOrDefault(session) ?? SpeakerNames.Default;
        _names[session] = speaker == Speaker.Me ? current with { Me = text } : current with { Others = text };
        return ValueTask.CompletedTask;
    }
}
