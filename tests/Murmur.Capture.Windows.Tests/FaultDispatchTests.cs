using Murmur.Capture.Windows.Screen;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Tests;

public sealed class FaultDispatchTests
{
    private sealed class Raiser
    {
        public event Action<CaptureFault>? Faulted;

        public void Queue(CaptureFault fault, List<Action> queue) => FaultDispatch.Queue(Faulted, fault, queue.Add);
    }

    [Fact]
    public void AFaultQueuedForOneSegment_NeverReachesTheSubscriberOfTheNextOne()
    {
        var raiser = new Raiser();
        var queue = new List<Action>();
        var first = new List<CaptureFault>();
        var second = new List<CaptureFault>();
        void FirstHandler(CaptureFault f) => first.Add(f);
        void SecondHandler(CaptureFault f) => second.Add(f);
        var fault = new CaptureFault("screen", "pool", IsFatal: false, CaptureFaultKind.FramesDropped);

        // WGC raises the fault of segment N; before the thread pool runs it, the recorder moves on to segment N+1.
        raiser.Faulted += FirstHandler;
        raiser.Queue(fault, queue);
        raiser.Faulted -= FirstHandler;
        raiser.Faulted += SecondHandler;
        foreach (var work in queue)
        {
            work();
        }

        Assert.Equal([fault], first);
        Assert.Empty(second);
    }

    [Fact]
    public void WithoutSubscribers_NothingIsQueued()
    {
        var queue = new List<Action>();

        new Raiser().Queue(new CaptureFault("screen", "x", IsFatal: true), queue);

        Assert.Empty(queue);
    }

    [Fact]
    public void ByDefault_TheHandlerRunsOnAThreadPoolThread()
    {
        var fault = new CaptureFault("screen", "x", IsFatal: false);
        using var done = new ManualResetEventSlim();
        CaptureFault? received = null;
        var thread = -1;

        FaultDispatch.Queue(f =>
        {
            received = f;
            thread = Environment.CurrentManagedThreadId;
            done.Set();
        }, fault);

        Assert.True(done.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Same(fault, received);
        Assert.NotEqual(Environment.CurrentManagedThreadId, thread);
    }
}
