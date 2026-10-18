namespace Murmur.Core.Capture.Synthetic;

/// <summary>An <see cref="IScreenRecorder"/> that creates each segment file empty and never captures a frame.</summary>
public sealed class NullScreenRecorder : IScreenRecorder
{
    private readonly List<string> _segments = [];
    private volatile bool _recording;

    public bool IsRecording => _recording;

    public ScreenRecorderStats Stats => new(0, 0, 0, "null");

    /// <summary>Segment paths in the order they were started.</summary>
    public IReadOnlyList<string> Segments
    {
        get
        {
            lock (_segments)
            {
                return [.. _segments];
            }
        }
    }

    public bool IsDisposed { get; private set; }

    /// <remarks>Never raised.</remarks>
    public event Action<CaptureFault>? Faulted
    {
        add { }
        remove { }
    }

    public ValueTask StartSegmentAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_recording)
        {
            throw new InvalidOperationException("A segment is already being recorded.");
        }

        File.Create(filePath).Dispose();
        lock (_segments)
        {
            _segments.Add(filePath);
        }

        _recording = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopSegmentAsync(CancellationToken cancellationToken)
    {
        _recording = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _recording = false;
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
