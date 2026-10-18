using Murmur.App.Services;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests.Fakes;

/// <summary>Scriptable <see cref="ISessionProcessing"/>; <see cref="Raise"/> simulates a pipeline event.</summary>
internal sealed class FakeProcessing : ISessionProcessing
{
    private readonly Dictionary<SessionId, SessionProcessingStatus> _statuses = [];

    public bool IsAsrInstalled { get; set; } = true;

    public string ModelsDirectory { get; set; } = "/models";

    public Exception? EnqueueFailure { get; set; }

    public bool EnqueueResult { get; set; } = true;

    public List<SessionId> Enqueued { get; } = [];

    public event Action<SessionProcessingStatus>? ProgressChanged;

    public SessionProcessingStatus? GetStatus(SessionId session) => _statuses.GetValueOrDefault(session);

    public ValueTask<bool> EnqueueAsync(SessionId session, CancellationToken cancellationToken)
    {
        Enqueued.Add(session);
        if (EnqueueFailure is { } failure)
        {
            throw failure;
        }

        return ValueTask.FromResult(EnqueueResult);
    }

    /// <summary>Stores the status (as the real processor does) and raises the event.</summary>
    public void Raise(SessionProcessingStatus status)
    {
        _statuses[status.Session] = status;
        ProgressChanged?.Invoke(status);
    }

    /// <summary>Stores the status without an event (the state before a screen opened).</summary>
    public void Set(SessionProcessingStatus status) => _statuses[status.Session] = status;
}
