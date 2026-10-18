using Murmur.Core.Processing;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.Services;

/// <summary>
/// What the screens need from the post-recording pipeline (<see cref="SessionProcessor"/>): live status, the
/// "is the ASR installed" answer and a way to (re-)queue a session. A small seam so view models are tested without a
/// job queue, a recorder and an ASR worker.
/// </summary>
public interface ISessionProcessing
{
    /// <summary>Raised whenever the processing status of a session changes, on a background thread.</summary>
    event Action<SessionProcessingStatus>? ProgressChanged;

    /// <summary>False while the Parakeet model files are missing.</summary>
    bool IsAsrInstalled { get; }

    /// <summary>Where the models are looked for (shown in the "install the model" card).</summary>
    string ModelsDirectory { get; }

    /// <summary>The last status of a session handled since start, or <c>null</c>.</summary>
    SessionProcessingStatus? GetStatus(SessionId session);

    /// <summary>Queues a session for (re-)transcription; <c>false</c> when it is already queued or still recording.</summary>
    ValueTask<bool> EnqueueAsync(SessionId session, CancellationToken cancellationToken);
}

/// <summary><see cref="ISessionProcessing"/> over the real <see cref="SessionProcessor"/>.</summary>
public sealed class SessionProcessingAdapter : ISessionProcessing
{
    private readonly SessionProcessor _processor;
    private readonly ModelLocator _models;

    public SessionProcessingAdapter(SessionProcessor processor, ModelLocator models)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(models);
        _processor = processor;
        _models = models;
    }

    public event Action<SessionProcessingStatus>? ProgressChanged
    {
        add => _processor.ProgressChanged += value;
        remove => _processor.ProgressChanged -= value;
    }

    public bool IsAsrInstalled => _processor.IsAsrInstalled;

    public string ModelsDirectory => _models.ModelsDirectory;

    public SessionProcessingStatus? GetStatus(SessionId session) => _processor.GetStatus(session);

    public ValueTask<bool> EnqueueAsync(SessionId session, CancellationToken cancellationToken) =>
        _processor.EnqueueAsync(session, cancellationToken);
}

/// <summary>No pipeline (designer, tests that do not care): nothing is ever processed.</summary>
internal sealed class NullSessionProcessing : ISessionProcessing
{
    public static readonly NullSessionProcessing Instance = new();

    public event Action<SessionProcessingStatus>? ProgressChanged
    {
        add { }
        remove { }
    }

    public bool IsAsrInstalled => true;

    public string ModelsDirectory => string.Empty;

    public SessionProcessingStatus? GetStatus(SessionId session) => null;

    public ValueTask<bool> EnqueueAsync(SessionId session, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}
