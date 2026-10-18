using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>Recycling of the CPU path's system-memory samples, on fake COM objects (any OS).</summary>
public sealed class MemorySamplePoolTests
{
    [Fact]
    public void Acquire_HandsOutIdleSamples_AndRecyclesThemOnceReleased()
    {
        using var factory = new FakeSampleFactory();
        using var pool = new MemorySamplePool(factory, 64, initial: 2, maximum: 4);
        Assert.Equal(2, pool.Count);
        Assert.Equal(4, pool.Capacity);
        Assert.Equal(64u, pool.BufferLength);

        Assert.Equal(ScreenConstants.SOk, pool.TryAcquire(out var first));
        Assert.Equal(factory.Samples[0].Pointer, first);
        Assert.Equal(2, factory.Samples[0].References); // pool + caller

        Assert.Equal(ScreenConstants.SOk, pool.TryAcquire(out var second));
        Assert.Equal(factory.Samples[1].Pointer, second); // the first is still held

        ComCall.Release(ref first);
        Assert.Equal(ScreenConstants.SOk, pool.TryAcquire(out var third));
        Assert.Equal(factory.Samples[0].Pointer, third); // idle again
        Assert.Equal(2, pool.Count);

        ComCall.Release(ref second);
        ComCall.Release(ref third);
    }

    [Fact]
    public void Acquire_SkipsSamplesOrBuffersStillHeldByMediaFoundation()
    {
        using var factory = new FakeSampleFactory();
        using var pool = new MemorySamplePool(factory, 64, initial: 2, maximum: 3);
        pool.TryAcquire(out var a);
        pool.TryAcquire(out var b);

        // The sink writer keeps sample a; the encoder keeps only the buffer of sample b (e.g. ConvertToContiguousBuffer).
        ComCall.AddRef(a);
        var heldBuffer = factory.Buffers[1].Pointer;
        ComCall.AddRef(heldBuffer);
        ComCall.Release(ref a);
        ComCall.Release(ref b);

        Assert.Equal(ScreenConstants.SOk, pool.TryAcquire(out var c));
        Assert.Equal(3, pool.Count); // neither a nor b was reusable: the pool grew
        Assert.Equal(factory.Samples[2].Pointer, c);

        Assert.Equal(ScreenConstants.MfESampleAllocatorEmpty, pool.TryAcquire(out var none));
        Assert.Equal(0, none);

        ComCall.ReleaseRaw(factory.Samples[0].Pointer); // the sink writer is done with a
        Assert.Equal(ScreenConstants.SOk, pool.TryAcquire(out var again));
        Assert.Equal(factory.Samples[0].Pointer, again);

        ComCall.ReleaseRaw(heldBuffer); // the encoder is done with b's buffer
        Assert.Equal(ScreenConstants.SOk, pool.TryAcquire(out var last));
        Assert.Equal(factory.Samples[1].Pointer, last);

        ComCall.Release(ref c);
        ComCall.Release(ref again);
        ComCall.Release(ref last);
    }

    [Fact]
    public void IdleReferenceCounts_AreCalibratedPerSample()
    {
        // A sample implementation that keeps a reference on itself is still recycled (and never handed out twice).
        using var factory = new FakeSampleFactory { InternalSampleReferences = 1 };
        using var pool = new MemorySamplePool(factory, 16, initial: 1, maximum: 1);

        Assert.Equal(ScreenConstants.SOk, pool.TryAcquire(out var first));
        Assert.Equal(ScreenConstants.MfESampleAllocatorEmpty, pool.TryAcquire(out _));
        ComCall.Release(ref first);
        Assert.Equal(ScreenConstants.SOk, pool.TryAcquire(out var second));
        ComCall.Release(ref second);
    }

    [Fact]
    public void Acquire_ReturnsTheFactoryError_WhenGrowingFails()
    {
        using var factory = new FakeSampleFactory();
        using var pool = new MemorySamplePool(factory, 16, initial: 0, maximum: 2);
        factory.Result = unchecked((int)0x8007000E); // E_OUTOFMEMORY

        Assert.Equal(unchecked((int)0x8007000E), pool.TryAcquire(out var sample));
        Assert.Equal(0, sample);
        Assert.Equal(0, pool.Count);

        factory.Result = 0;
        Assert.Equal(ScreenConstants.SOk, pool.TryAcquire(out sample));
        Assert.Equal(1, pool.Count);
        ComCall.Release(ref sample);
    }

    [Fact]
    public void Constructor_Throws_AndReleasesWhatItCreated_WhenTheInitialSamplesFail()
    {
        using var factory = new FailingAfterFactory(succeed: 1);
        var ex = Assert.Throws<COMException>(() => new MemorySamplePool(factory, 16, initial: 3, maximum: 3));
        Assert.Equal(unchecked((int)0x8007000E), ex.HResult);
        Assert.Single(factory.Inner.Samples);
        Assert.All(factory.Inner.Samples, s => Assert.Equal(0, s.References));
        Assert.All(factory.Inner.Buffers, b => Assert.Equal(1, b.References)); // only the (fake) sample's buffer list is left
    }

    [Fact]
    public void TryGetBuffer_KnowsOnlyThePoolsSamples()
    {
        using var factory = new FakeSampleFactory();
        using var pool = new MemorySamplePool(factory, 16, initial: 1, maximum: 1);
        using var stranger = new FakeSample();

        Assert.True(pool.TryGetBuffer(factory.Samples[0].Pointer, out var buffer));
        Assert.Equal(factory.Buffers[0].Pointer, buffer);
        Assert.False(pool.TryGetBuffer(stranger.Pointer, out buffer));
        Assert.Equal(0, buffer);
    }

    [Fact]
    public void Dispose_DropsThePoolReferences()
    {
        using var factory = new FakeSampleFactory();
        var pool = new MemorySamplePool(factory, 16, initial: 2, maximum: 2);
        Assert.Equal(2, factory.Buffers[0].References); // pool + the sample's buffer list

        pool.Dispose();
        pool.Dispose();

        Assert.All(factory.Samples, s => Assert.Equal(0, s.References));
        Assert.All(factory.Buffers, b => Assert.Equal(1, b.References)); // the (fake) sample still lists its buffer
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void Acquire_SteadyState_DoesNotAllocate()
    {
        using var factory = new FakeSampleFactory();
        using var pool = new MemorySamplePool(factory, 16, initial: 2, maximum: 2);
        void Cycle()
        {
            for (var i = 0; i < 100; i++)
            {
                pool.TryAcquire(out var sample);
                pool.TryGetBuffer(sample, out _);
                ComCall.Release(ref sample);
            }
        }

        Cycle();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Cycle();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Constructor_ValidatesArguments()
    {
        using var factory = new FakeSampleFactory();
        Assert.Throws<ArgumentNullException>(() => new MemorySamplePool(null!, 16, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemorySamplePool(factory, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemorySamplePool(factory, 16, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemorySamplePool(factory, 16, 3, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemorySamplePool(factory, 16, 0, 0));
    }

    private sealed class FailingAfterFactory(int succeed) : IMemorySampleFactory, IDisposable
    {
        private int _left = succeed;

        public FakeSampleFactory Inner { get; } = new();

        public int Create(uint length, out nint sample, out nint buffer)
        {
            if (_left-- > 0)
            {
                return Inner.Create(length, out sample, out buffer);
            }

            sample = buffer = 0;
            return unchecked((int)0x8007000E);
        }

        public void Dispose() => Inner.Dispose();
    }
}
