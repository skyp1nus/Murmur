using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// Ownership hand-offs of the held sample (WgcScreenRecorder's WriteHeld and Repeat) on fake COM objects: every path
/// releases exactly the references it was given, managed exceptions included, and a failed repeat copy is told apart
/// from an empty pool.
/// </summary>
public sealed class HeldSampleTests : IDisposable
{
    private const int DeviceRemoved = unchecked((int)0x887A0005); // DXGI_ERROR_DEVICE_REMOVED
    private const int SinkFailure = unchecked((int)0xC00D36B2); // MF_E_INVALIDREQUEST

    private readonly List<FakeCom> _objects = [];

    public void Dispose()
    {
        foreach (var fake in _objects)
        {
            fake.Dispose();
        }
    }

    [Fact]
    public void Write_Success_TimestampsWritesAndReleasesTheHeldSample()
    {
        var sample = Track(new FakeSample());
        var held = Own(sample);
        var sink = new FakeSink();

        var hr = HeldSample.Write(ref held, sink, 3_330_000, 333_667, out var step, out var exception);

        Assert.Equal(ScreenConstants.SOk, hr);
        Assert.Equal(HeldSample.WriteSampleStep, step);
        Assert.Null(exception);
        Assert.Equal(0, held);
        Assert.Equal([sample.Pointer], sink.Written);
        Assert.Equal(3_330_000, sample.Time);
        Assert.Equal([3_330_000L], sink.Times); // the sink is told the sample time (keyframe schedule)
        Assert.Equal(333_667, sample.Duration);
        Assert.Equal(1, sample.References); // only the test's own reference is left
    }

    [Fact]
    public void Write_SinkFails_ReturnsItsHResult_AndReleasesTheSample()
    {
        var sample = Track(new FakeSample());
        var held = Own(sample);

        var hr = HeldSample.Write(ref held, new FakeSink { Result = SinkFailure }, 0, 1, out var step, out var exception);

        Assert.Equal(SinkFailure, hr);
        Assert.Equal(HeldSample.WriteSampleStep, step);
        Assert.Null(exception);
        Assert.Equal(0, held);
        Assert.Equal(1, sample.References);
    }

    [Fact]
    public void Write_SetSampleTimeFails_NamesTheStep_DoesNotWrite_AndReleasesTheSample()
    {
        var sample = Track(new BareSample()); // every IMFSample method answers E_NOTIMPL
        var held = Own(sample);
        var sink = new FakeSink();

        var hr = HeldSample.Write(ref held, sink, 0, 1, out var step, out _);

        Assert.Equal(FakeCom.ENotImpl, hr);
        Assert.Equal(HeldSample.SetSampleTimeStep, step);
        Assert.Empty(sink.Written);
        Assert.Equal(0, held);
        Assert.Equal(1, sample.References);
    }

    [Fact]
    public void Write_SinkThrows_ReturnsTheExceptionsHResult_AndReleasesTheSample()
    {
        // Before the fix the held field was cleared first and the sample leaked when WriteSample threw.
        var sample = Track(new FakeSample());
        var held = Own(sample);
        var thrown = new InvalidOperationException("sink gone");

        var hr = HeldSample.Write(ref held, new FakeSink { Throw = thrown }, 0, 1, out var step, out var exception);

        Assert.Equal(thrown.HResult, hr);
        Assert.True(hr < 0);
        Assert.Same(thrown, exception);
        Assert.Equal(HeldSample.WriteSampleStep, step);
        Assert.Equal(0, held);
        Assert.Equal(1, sample.References);
    }

    [Fact]
    public void Write_ExceptionWithoutAFailureHResult_IsReportedAsEFail()
    {
        var sample = Track(new FakeSample());
        var held = Own(sample);
        var thrown = new InvalidOperationException("odd") { HResult = 0 };

        var hr = HeldSample.Write(ref held, new FakeSink { Throw = thrown }, 0, 1, out _, out var exception);

        Assert.Equal(ScreenConstants.EFail, hr);
        Assert.Same(thrown, exception);
        Assert.Equal(1, sample.References);
    }

    [Fact]
    public void Write_WithoutASink_DoesNotThrow_AndReleasesTheSample()
    {
        var sample = Track(new FakeSample());
        var held = Own(sample);

        var hr = HeldSample.Write(ref held, null, 0, 1, out var step, out var exception);

        Assert.True(hr < 0);
        Assert.IsType<NullReferenceException>(exception);
        Assert.Equal(HeldSample.WriteSampleStep, step);
        Assert.Equal(0, held);
        Assert.Equal(1, sample.References);
    }

    [Fact]
    public void Write_Success_AllocatesNothing()
    {
        var sample = Track(new FakeSample());
        var sink = new FakeSink { Record = false };
        WriteMany(sample, sink, 16); // warm-up (JIT, dictionary lookups)

        var before = GC.GetAllocatedBytesForCurrentThread();
        WriteMany(sample, sink, 1_000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(1, sample.References);
    }

    [Fact]
    public void CopyForRepeat_Success_HandsOverTheCopy_AndKeepsTheHeldSample()
    {
        var held = Track(new FakeSample());
        var free = Track(new FakeSample());
        using var frames = new FakeFramePath(free);

        var outcome = HeldSample.CopyForRepeat(frames, new Lock(), held.Pointer, out var copy, out var hr, out _);

        Assert.Equal(RepeatCopy.Copied, outcome);
        Assert.True(hr >= 0);
        Assert.Equal(free.Pointer, copy);
        Assert.Equal([(free.Pointer, held.Pointer)], frames.Copies);
        Assert.Equal(2, free.References); // the caller now owns the allocator's reference
        Assert.Equal(1, held.References); // untouched
    }

    [Fact]
    public void CopyForRepeat_EmptyPool_IsASkip_NotAFault()
    {
        var held = Track(new FakeSample());
        using var frames = new FakeFramePath();

        var outcome = HeldSample.CopyForRepeat(frames, new Lock(), held.Pointer, out var copy, out var hr, out _);

        Assert.Equal(RepeatCopy.PoolEmpty, outcome);
        Assert.Equal(ScreenConstants.MfESampleAllocatorEmpty, hr);
        Assert.Equal(0, copy);
        Assert.Empty(frames.Copies);
        Assert.Equal(1, held.References);
    }

    [Fact]
    public void CopyForRepeat_FailedCopy_IsAFailure_AndReleasesTheNewSample()
    {
        // A device removed while the screen is static must fault like a failed captured frame, not count as a skip.
        var held = Track(new FakeSample());
        var free = Track(new FakeSample());
        using var frames = new FakeFramePath(free) { CopyResult = DeviceRemoved, CopyFailureStep = FrameStep.BufferLength };

        var outcome = HeldSample.CopyForRepeat(frames, new Lock(), held.Pointer, out var copy, out var hr, out var failure);

        Assert.Equal(RepeatCopy.CopyFailed, outcome);
        Assert.Equal(DeviceRemoved, hr);
        Assert.Equal(new FrameFailure(FrameStep.BufferLength, DeviceRemoved), failure);
        Assert.Equal(0, copy);
        Assert.Equal(1, free.References);
        Assert.Equal(1, held.References);
        Assert.Equal(
            "Repeating the held frame failed: GPU frame copy failed at IMFMediaBuffer::SetCurrentLength: HRESULT 0x887A0005",
            HeldSample.RepeatFault(outcome, EncoderInput.Gpu, failure, hr));
    }

    [Fact]
    public void CopyForRepeat_FailedAllocation_IsAFailure()
    {
        var held = Track(new FakeSample());
        using var frames = new FakeFramePath { AllocateResult = DeviceRemoved };

        var outcome = HeldSample.CopyForRepeat(frames, new Lock(), held.Pointer, out var copy, out var hr, out var failure);

        Assert.Equal(RepeatCopy.AllocateFailed, outcome);
        Assert.Equal(DeviceRemoved, hr);
        Assert.Equal(0, copy);
        Assert.Equal(
            "Allocating an encoder sample to repeat the held frame failed (CPU readback): HRESULT 0x887A0005",
            HeldSample.RepeatFault(outcome, EncoderInput.Cpu, failure, hr));
    }

    [Fact]
    public void CopyForRepeat_AllocationSucceedingWithoutASample_IsAFailure()
    {
        var held = Track(new FakeSample());
        using var frames = new FakeFramePath { AllocateResult = ScreenConstants.SOk };

        var outcome = HeldSample.CopyForRepeat(frames, new Lock(), held.Pointer, out var copy, out var hr, out _);

        Assert.Equal(RepeatCopy.AllocateFailed, outcome);
        Assert.Equal(ScreenConstants.EPointer, hr);
        Assert.Equal(0, copy);
    }

    [Fact]
    public void CopyForRepeat_CopyThrows_ReleasesTheNewSample_AndRethrows()
    {
        var held = Track(new FakeSample());
        var free = Track(new FakeSample());
        using var frames = new FakeFramePath(free) { CopyThrows = true };
        var copyLock = new Lock();

        Assert.Throws<InvalidOperationException>(() => HeldSample.CopyForRepeat(frames, copyLock, held.Pointer, out _, out _, out _));

        Assert.Equal(1, free.References);
        Assert.Equal(1, held.References);
        Assert.False(copyLock.IsHeldByCurrentThread);
    }

    [Fact]
    public void RepeatFault_RejectsOutcomesThatAreNotFailures()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HeldSample.RepeatFault(RepeatCopy.Copied, EncoderInput.Gpu, default, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => HeldSample.RepeatFault(RepeatCopy.PoolEmpty, EncoderInput.Gpu, default, 0));
    }

    private static nint Own(FakeCom sample)
    {
        ComCall.AddRef(sample.Pointer); // the reference the held field owns
        return sample.Pointer;
    }

    private static void WriteMany(FakeSample sample, FakeSink sink, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var held = Own(sample);
            var hr = HeldSample.Write(ref held, sink, i * 333_667L, 333_667, out _, out _);
            if (hr < 0 || held != 0)
            {
                Assert.Fail($"write {i} failed: 0x{hr:X8}");
            }
        }
    }

    private T Track<T>(T fake)
        where T : FakeCom
    {
        _objects.Add(fake);
        return fake;
    }

    /// <summary>A COM object with the IMFSample slot count but no implemented method (E_NOTIMPL everywhere).</summary>
    private sealed class BareSample() : FakeCom(47);

    private sealed class FakeSink : ISampleSink
    {
        public List<nint> Written { get; } = [];

        public List<long> Times { get; } = [];

        public bool Record { get; init; } = true;

        public int Result { get; init; }

        public Exception? Throw { get; init; }

        public int WriteSample(nint sample, long time)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            if (Record)
            {
                Written.Add(sample);
                Times.Add(time);
            }

            return Result;
        }
    }

    /// <summary>
    /// An <see cref="IFramePath"/> handing out the given samples (one reference each, MF_E_SAMPLEALLOCATOR_EMPTY when
    /// none is left, or <see cref="AllocateResult"/>) and recording copies.
    /// </summary>
    private sealed class FakeFramePath(params FakeSample[] samples) : IFramePath
    {
        private readonly Queue<FakeSample> _available = new(samples);

        public EncoderInput Input => EncoderInput.Gpu;

        public FrameFailure LastFailure { get; private set; }

        public int? AllocateResult { get; init; }

        public int CopyResult { get; init; }

        public FrameStep CopyFailureStep { get; init; }

        public bool CopyThrows { get; init; }

        public List<(nint Destination, nint Source)> Copies { get; } = [];

        public int AllocateSample(out nint sample)
        {
            sample = 0;
            if (AllocateResult is { } result)
            {
                return result;
            }

            if (!_available.TryDequeue(out var next))
            {
                return ScreenConstants.MfESampleAllocatorEmpty;
            }

            ComCall.AddRef(next.Pointer);
            sample = next.Pointer;
            return ScreenConstants.SOk;
        }

        public int CopySample(nint destination, nint source)
        {
            if (CopyThrows)
            {
                throw new InvalidOperationException("copy blew up");
            }

            Copies.Add((destination, source));
            if (CopyResult < 0)
            {
                LastFailure = new FrameFailure(CopyFailureStep, CopyResult);
            }

            return CopyResult;
        }

        public int CopyFrame(nint sample, nint source, PixelSize content) => throw new NotSupportedException();

        public int FillBlack(nint sample) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
