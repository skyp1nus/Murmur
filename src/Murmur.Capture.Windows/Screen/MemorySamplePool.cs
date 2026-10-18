using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Screen;

/// <summary>Creates one system-memory sample holding one media buffer (the caller owns one reference to each).</summary>
internal interface IMemorySampleFactory
{
    /// <summary>Returns an HRESULT; on success <paramref name="sample"/> holds <paramref name="buffer"/> (a buffer of at least <paramref name="length"/> bytes).</summary>
    int Create(uint length, out nint sample, out nint buffer);
}

/// <summary>MFCreateSample + MFCreateMemoryBuffer + IMFSample::AddBuffer.</summary>
internal sealed unsafe class MediaFoundationSampleFactory : IMemorySampleFactory
{
    public static readonly MediaFoundationSampleFactory Instance = new();

    private MediaFoundationSampleFactory()
    {
    }

    public int Create(uint length, out nint sample, out nint buffer)
    {
        nint s = 0, b = 0;
        var hr = ScreenNativeMethods.MFCreateSample(&s);
        if (hr >= 0)
        {
            hr = ScreenNativeMethods.MFCreateMemoryBuffer(length, &b);
        }

        if (hr >= 0)
        {
            hr = IMFSample.AddBuffer(s, b);
        }

        if (hr < 0)
        {
            ComCall.Release(ref b);
            ComCall.Release(ref s);
        }

        sample = s;
        buffer = b;
        return hr;
    }
}

/// <summary>
/// A bounded set of system-memory samples (one buffer each) recycled without COM callbacks, for the CPU readback path.
/// A sample is handed out again only when nothing but the pool references it or its buffer: the idle reference counts
/// of every new sample and buffer are measured once (AddRef / Release return the new count for Media Foundation's
/// in-process objects) and compared on every acquire, so a sample the sink writer, the encoder or the recorder (last
/// frame kept for repeats) still holds is never overwritten. Starts with <c>initial</c> samples and grows on demand up to
/// <c>maximum</c>; when all are busy <see cref="TryAcquire"/> reports MF_E_SAMPLEALLOCATOR_EMPTY (back-pressure).
/// Allocation-free once grown. Not thread-safe: callers serialize (the recorder's copy lock); concurrent releases by
/// Media Foundation threads are fine (a sample can only become idle behind our back, never busy).
/// </summary>
internal sealed class MemorySamplePool : IDisposable
{
    private readonly IMemorySampleFactory _factory;
    private readonly Slot[] _slots;
    private int _count;

    /// <exception cref="COMException">Creating the initial samples failed.</exception>
    public MemorySamplePool(IMemorySampleFactory factory, uint bufferLength, int initial, int maximum)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentOutOfRangeException.ThrowIfZero(bufferLength);
        ArgumentOutOfRangeException.ThrowIfNegative(initial);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, Math.Max(1, initial));
        _factory = factory;
        BufferLength = bufferLength;
        _slots = new Slot[maximum];
        try
        {
            for (var i = 0; i < initial; i++)
            {
                var hr = Grow();
                if (hr < 0)
                {
                    throw new COMException($"Creating a system-memory sample of {bufferLength} bytes failed: HRESULT 0x{hr:X8}", hr);
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Bytes of every sample buffer.</summary>
    public uint BufferLength { get; }

    /// <summary>Samples created so far.</summary>
    public int Count => _count;

    /// <summary>Upper bound of <see cref="Count"/>.</summary>
    public int Capacity => _slots.Length;

    /// <summary>
    /// Takes an idle sample, creating one if all are busy and the pool may still grow. The caller owns one reference
    /// to <paramref name="sample"/> and releases it when done. Returns MF_E_SAMPLEALLOCATOR_EMPTY when every sample is
    /// busy, or the factory's HRESULT when growing failed.
    /// </summary>
    public int TryAcquire(out nint sample)
    {
        for (var i = 0; i < _count; i++)
        {
            ref var slot = ref _slots[i];
            if (IsIdle(in slot))
            {
                ComCall.AddRef(slot.Sample);
                sample = slot.Sample;
                return ScreenConstants.SOk;
            }
        }

        if (_count == _slots.Length)
        {
            sample = 0;
            return ScreenConstants.MfESampleAllocatorEmpty;
        }

        var hr = Grow();
        if (hr < 0)
        {
            sample = 0;
            return hr;
        }

        sample = _slots[_count - 1].Sample;
        ComCall.AddRef(sample);
        return ScreenConstants.SOk;
    }

    /// <summary>The buffer of a sample of this pool (a borrowed pointer: no reference is added).</summary>
    public bool TryGetBuffer(nint sample, out nint buffer)
    {
        for (var i = 0; i < _count; i++)
        {
            if (_slots[i].Sample == sample)
            {
                buffer = _slots[i].Buffer;
                return true;
            }
        }

        buffer = 0;
        return false;
    }

    public void Dispose()
    {
        for (var i = 0; i < _count; i++)
        {
            ComCall.Release(ref _slots[i].Buffer);
            ComCall.Release(ref _slots[i].Sample);
        }

        _count = 0;
    }

    private static bool IsIdle(in Slot slot) =>
        ReferenceCount(slot.Sample) == slot.IdleSampleReferences && ReferenceCount(slot.Buffer) == slot.IdleBufferReferences;

    /// <summary>The current reference count (AddRef returns the new count; the probe reference is dropped again).</summary>
    private static uint ReferenceCount(nint unknown)
    {
        var count = ComCall.AddRef(unknown);
        ComCall.ReleaseRaw(unknown);
        return count - 1;
    }

    private int Grow()
    {
        var hr = _factory.Create(BufferLength, out var sample, out var buffer);
        if (hr < 0)
        {
            return hr;
        }

        if (sample == 0 || buffer == 0)
        {
            ComCall.Release(ref buffer);
            ComCall.Release(ref sample);
            return ScreenConstants.EPointer;
        }

        _slots[_count++] = new Slot
        {
            Sample = sample,
            Buffer = buffer,
            IdleSampleReferences = ReferenceCount(sample),
            IdleBufferReferences = ReferenceCount(buffer),
        };
        return ScreenConstants.SOk;
    }

    private struct Slot
    {
        public nint Sample;
        public nint Buffer;
        public uint IdleSampleReferences;
        public uint IdleBufferReferences;
    }
}
