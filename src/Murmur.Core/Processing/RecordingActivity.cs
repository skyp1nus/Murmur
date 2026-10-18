using Murmur.Core.Recording;

namespace Murmur.Core.Processing;

/// <summary>
/// Glue between the recorder and the LLM recording guard. Murmur.Core does not reference Murmur.Intelligence, so the
/// composition root subscribes <see cref="RecordingStarted"/> and calls <c>LocalLlm.NotifyRecordingStarted()</c>
/// (which stops the llama-server processes), and passes <see cref="IsRecording"/> to whatever must not start work
/// while a recording is running.
/// </summary>
/// <remarks>
/// "Recording" means the recorder is not idle (starting, recording or stopping): models must stay out of memory for
/// the whole span. <see cref="RecordingStarted"/> is raised on the recorder's thread, once per recording (on the
/// transition out of Idle); handlers must be quick and must not throw (a throwing handler is swallowed so it can never
/// break the recorder).
/// </remarks>
public sealed class RecordingActivity : IDisposable
{
    private readonly IRecorder _recorder;
    private int _active;
    private int _disposed;

    public RecordingActivity(IRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        _recorder = recorder;
        _active = recorder.State != RecorderState.Idle ? 1 : 0;
        recorder.StateChanged += OnStateChanged;
    }

    /// <summary>Raised when a recording begins (the recorder leaves Idle).</summary>
    public event Action? RecordingStarted;

    /// <summary>Raised when the recorder is back to Idle after a recording.</summary>
    public event Action? RecordingStopped;

    /// <summary><c>true</c> while the recorder is not idle.</summary>
    public bool IsRecording => _recorder.State != RecorderState.Idle;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _recorder.StateChanged -= OnStateChanged;
        }
    }

    private void OnStateChanged(RecorderState state)
    {
        var active = state != RecorderState.Idle;
        if (Interlocked.Exchange(ref _active, active ? 1 : 0) == (active ? 1 : 0))
        {
            return;
        }

        try
        {
            (active ? RecordingStarted : RecordingStopped)?.Invoke();
        }
        catch (Exception)
        {
            // Never let a subscriber break the recorder.
        }
    }
}
