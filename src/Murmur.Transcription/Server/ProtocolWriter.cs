using System.Text.Json;
using Murmur.Core.Transcription.Protocol;

namespace Murmur.Transcription.Server;

/// <summary>Writes one <see cref="WorkerEvent"/> per line and flushes, from any thread.</summary>
internal sealed class ProtocolWriter(TextWriter output)
{
    private readonly Lock _gate = new();

    public void Write(WorkerEvent e)
    {
        var line = JsonSerializer.Serialize(e, WorkerJsonContext.Default.WorkerEvent);
        lock (_gate)
        {
            output.Write(line);
            output.Write('\n');
            output.Flush();
        }
    }

    public void Error(string? requestId, string message) =>
        Write(new WorkerEvent { Kind = WorkerKinds.Error, RequestId = requestId, Error = message });
}
