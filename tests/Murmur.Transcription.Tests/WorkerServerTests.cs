using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Murmur.Core.Transcription.Protocol;
using Murmur.Transcription.Server;

namespace Murmur.Transcription.Tests;

/// <summary>In-process protocol tests of <see cref="WorkerServer"/> with a fake engine (no models needed).</summary>
public sealed class WorkerServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private sealed class Harness : IDisposable
    {
        public Harness(FakeEngine engine, TimeSpan? idle = null, TextWriter? output = null)
        {
            Engine = engine;
            var server = new WorkerServer(Input, output ?? Output, TextWriter.Null, () => engine, idle ?? TimeSpan.Zero);
            Exit = Task.Factory.StartNew(server.Run, TaskCreationOptions.LongRunning);
        }

        public FakeEngine Engine { get; }

        public LineReader Input { get; } = new();

        public LineWriter Output { get; } = new();

        public Task<int> Exit { get; }

        public void Send(string json) => Input.Push(json);

        public void Send(WorkerRequest r) => Input.Push(JsonSerializer.Serialize(r, WorkerJsonContext.Default.WorkerRequest));

        public WorkerEvent Next()
        {
            Assert.True(Output.Lines.TryTake(out var line, Timeout), "timed out waiting for an event");
            return JsonSerializer.Deserialize(line!, WorkerJsonContext.Default.WorkerEvent)!;
        }

        /// <summary>Reads events until one of <paramref name="kinds"/> arrives for the request.</summary>
        public List<WorkerEvent> UntilTerminal(string requestId)
        {
            var events = new List<WorkerEvent>();
            while (true)
            {
                var e = Next();
                events.Add(e);
                if (e.RequestId == requestId && e.Kind is WorkerKinds.Done or WorkerKinds.Error)
                {
                    return events;
                }
            }
        }

        public int WaitExit()
        {
            Assert.True(Exit.Wait(Timeout), "server did not exit");
            return Exit.Result;
        }

        public void Dispose() => Input.Close();
    }

    private static WorkerRequest Transcribe(string id, string path = "/a.wav") =>
        new() { Kind = WorkerKinds.Transcribe, RequestId = id, AudioPath = path };

    [Fact]
    public void Ready_ThenPingPong_ThenShutdownExits0()
    {
        using var h = new Harness(new FakeEngine());

        Assert.Equal(WorkerKinds.Ready, h.Next().Kind);
        h.Send(new WorkerRequest { Kind = WorkerKinds.Ping, RequestId = "p1" });
        var pong = h.Next();
        Assert.Equal((WorkerKinds.Pong, "p1"), (pong.Kind, pong.RequestId));

        h.Send(new WorkerRequest { Kind = WorkerKinds.Shutdown });
        Assert.Equal(0, h.WaitExit());
        Assert.False(h.Engine.Disposed); // never created: the model loads lazily on the first transcribe
    }

    [Fact]
    public void Transcribe_StreamsSegmentsAndProgress_ThenDone()
    {
        using var h = new Harness(new FakeEngine { Batches = 3 });
        h.Next();

        h.Send(Transcribe("j1"));
        var events = h.UntilTerminal("j1");

        Assert.Equal(
            [WorkerKinds.Segment, WorkerKinds.Progress, WorkerKinds.Segment, WorkerKinds.Progress, WorkerKinds.Segment, WorkerKinds.Progress, WorkerKinds.Done],
            events.Select(e => e.Kind));
        Assert.All(events, e => Assert.Equal("j1", e.RequestId));
        Assert.Equal([0.0, 1.0, 2.0], events.Where(e => e.Segment is not null).Select(e => e.Segment!.Start));
        Assert.Equal("text", events[0].Segment!.Words[0].Text);
        Assert.Equal(3, events[^1].TotalSeconds);

        h.Send(new WorkerRequest { Kind = WorkerKinds.Shutdown });
        Assert.Equal(0, h.WaitExit());
        Assert.True(h.Engine.Disposed);
    }

    [Fact]
    public void MalformedAndUnknownRequests_YieldErrors_AndTheServerKeepsRunning()
    {
        using var h = new Harness(new FakeEngine());
        h.Next();

        h.Send("{not json");
        var e1 = h.Next();
        Assert.Equal(WorkerKinds.Error, e1.Kind);
        Assert.StartsWith("malformed request", e1.Error, StringComparison.Ordinal);

        h.Send("{\"requestId\":\"x\"}"); // kind is required
        Assert.StartsWith("malformed request", h.Next().Error, StringComparison.Ordinal);

        h.Send("{\"kind\":\"dance\",\"requestId\":\"d\"}");
        var e3 = h.Next();
        Assert.Equal(("d", WorkerKinds.Error), (e3.RequestId, e3.Kind));
        Assert.Contains("dance", e3.Error, StringComparison.Ordinal);

        h.Send(new WorkerRequest { Kind = WorkerKinds.Transcribe, RequestId = "t" });
        Assert.Contains("audioPath", h.Next().Error, StringComparison.Ordinal);

        h.Send("   ");
        h.Send(new WorkerRequest { Kind = WorkerKinds.Ping });
        Assert.Equal(WorkerKinds.Pong, h.Next().Kind);
    }

    [Fact]
    public void JobFailure_IsReportedForThatRequest_AndLaterJobsStillRun()
    {
        using var h = new Harness(new FakeEngine());
        h.Next();

        h.Send(Transcribe("bad", "/x/bad.wav"));
        h.Send(Transcribe("good"));

        var bad = h.UntilTerminal("bad")[^1];
        Assert.Equal(WorkerKinds.Error, bad.Kind);
        Assert.Contains("44100 Hz", bad.Error, StringComparison.Ordinal);
        Assert.Equal(WorkerKinds.Done, h.UntilTerminal("good")[^1].Kind);
    }

    [Fact]
    public void Jobs_RunOneAtATime_InArrivalOrder()
    {
        using var h = new Harness(new FakeEngine { Batches = 2 });
        h.Next();

        h.Send(Transcribe("a", "/a.wav"));
        h.Send(Transcribe("b", "/b.wav"));
        h.Send(Transcribe("c", "/c.wav"));
        var events = h.UntilTerminal("c");

        var order = events.Where(e => e.Kind == WorkerKinds.Done).Select(e => e.RequestId).ToArray();
        Assert.Equal(["a", "b", "c"], order);
        // no interleaving: all events of a precede all events of b, etc.
        var ids = events.Select(e => e.RequestId).ToArray();
        Assert.Equal(ids.OrderBy(i => i, StringComparer.Ordinal), ids);
        Assert.Equal(["/a.wav", "/b.wav", "/c.wav"], h.Engine.Jobs);
    }

    [Fact]
    public void Cancel_OfTheRunningJob_StopsItWithErrorCancelled()
    {
        using var gate = new ManualResetEventSlim(false);
        using var h = new Harness(new FakeEngine { Batches = 100, Gate = gate });
        h.Next();

        h.Send(Transcribe("long"));
        SpinWait.SpinUntil(() => !h.Engine.Jobs.IsEmpty, Timeout);
        h.Send(new WorkerRequest { Kind = WorkerKinds.Cancel, RequestId = "long" });

        var events = h.UntilTerminal("long");
        Assert.Equal(WorkerKinds.Error, events[^1].Kind);
        Assert.Equal(WorkerServer.CancelledError, events[^1].Error);
        Assert.DoesNotContain(events, e => e.Kind == WorkerKinds.Done);

        h.Send(new WorkerRequest { Kind = WorkerKinds.Ping, RequestId = "after" });
        Assert.Equal("after", h.Next().RequestId);
    }

    [Fact]
    public void Cancel_OfAQueuedJob_RemovesIt_AndUnknownIdsAreIgnored()
    {
        using var gate = new ManualResetEventSlim(false);
        using var h = new Harness(new FakeEngine { Batches = 1, Gate = gate });
        h.Next();

        h.Send(Transcribe("first"));
        h.Send(Transcribe("second"));
        h.Send(new WorkerRequest { Kind = WorkerKinds.Cancel, RequestId = "second" });
        var cancelled = h.Next();
        Assert.Equal(("second", WorkerKinds.Error, WorkerServer.CancelledError), (cancelled.RequestId, cancelled.Kind, cancelled.Error));

        h.Send(new WorkerRequest { Kind = WorkerKinds.Cancel, RequestId = "nope" });
        h.Send(new WorkerRequest { Kind = WorkerKinds.Cancel });
        Assert.Contains("requestId", h.Next().Error, StringComparison.Ordinal); // only the id-less cancel answers

        gate.Set();
        Assert.Equal(WorkerKinds.Done, h.UntilTerminal("first")[^1].Kind);
        Assert.Equal(["/a.wav"], h.Engine.Jobs);
    }

    [Fact]
    public void StdinEof_CancelsRunningAndQueuedJobs_AndExits0()
    {
        using var gate = new ManualResetEventSlim(false);
        using var h = new Harness(new FakeEngine { Batches = 5, Gate = gate });
        h.Next();
        h.Send(Transcribe("run"));
        SpinWait.SpinUntil(() => !h.Engine.Jobs.IsEmpty, Timeout);
        h.Send(Transcribe("queued"));
        SpinWait.SpinUntil(() => h.Input.Pending == 0, Timeout);
        Thread.Sleep(100);

        h.Input.Close();

        Assert.Equal(0, h.WaitExit());
        var rest = new List<WorkerEvent>();
        while (h.Output.Lines.TryTake(out var line))
        {
            rest.Add(JsonSerializer.Deserialize(line, WorkerJsonContext.Default.WorkerEvent)!);
        }

        Assert.Contains(rest, e => e.RequestId == "queued" && e.Error == WorkerServer.CancelledError);
        Assert.Contains(rest, e => e.RequestId == "run" && e.Error == WorkerServer.CancelledError);
    }

    [Fact]
    public void IdleTimeout_ExitsWith0_ButNotWhileAJobRuns()
    {
        using var gate = new ManualResetEventSlim(false);
        using var h = new Harness(new FakeEngine { Batches = 1, Gate = gate }, idle: TimeSpan.FromMilliseconds(300));
        h.Next();
        h.Send(Transcribe("slow"));

        Thread.Sleep(800);
        Assert.False(h.Exit.IsCompleted, "exited while a job was running");

        gate.Set();
        Assert.Equal(WorkerKinds.Done, h.UntilTerminal("slow")[^1].Kind);
        Assert.Equal(0, h.WaitExit());
    }

    [Fact]
    public void BrokenStdout_IsFatal_Exit1()
    {
        using var h = new Harness(new FakeEngine(), output: new ThrowingWriter(okWrites: 3));

        h.Send(new WorkerRequest { Kind = WorkerKinds.Ping });

        Assert.Equal(1, h.WaitExit());
    }

    /// <summary>Blocking line source standing in for stdin.</summary>
    internal sealed class LineReader : TextReader
    {
        private readonly BlockingCollection<string> _lines = [];

        public int Pending => _lines.Count;

        public void Push(string line) => _lines.Add(line);

        public new void Close() => _lines.CompleteAdding();

        public override string? ReadLine()
        {
            try
            {
                return _lines.Take();
            }
            catch (InvalidOperationException)
            {
                return null; // completed = EOF
            }
        }
    }

    /// <summary>Collects complete lines written to it (stands in for stdout).</summary>
    internal sealed class LineWriter : TextWriter
    {
        private readonly StringBuilder _partial = new();

        public BlockingCollection<string> Lines { get; } = [];

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_partial)
            {
                if (value == '\n')
                {
                    Lines.Add(_partial.ToString());
                    _partial.Clear();
                }
                else
                {
                    _partial.Append(value);
                }
            }
        }

        public override void Write(string? value)
        {
            foreach (var c in value ?? string.Empty)
            {
                Write(c);
            }
        }
    }

    private sealed class ThrowingWriter(int okWrites) : TextWriter
    {
        private int _writes;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Write(value.ToString());

        public override void Write(string? value)
        {
            if (++_writes > okWrites)
            {
                throw new IOException("broken pipe");
            }
        }
    }
}
