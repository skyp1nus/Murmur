using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Screen;

/// <summary>
/// CPU readback frame path (<see cref="EncoderInput.Cpu"/>) for machines without a hardware H.264 encoder or without a
/// real GPU (WARP, the Microsoft Basic Render Driver of GPU-less VMs and CI runners): the capture texture is copied into
/// a staging texture on the GPU, mapped, and copied (or bilinearly scaled and letterboxed, see <see cref="BgraScaler"/>)
/// into a pooled system-memory RGB32 sample (<see cref="MemorySamplePool"/>) for the software encoder. This needs
/// neither D3D11 video support nor Media Foundation's D3D11 pipeline; the software encoder reads system memory anyway.
/// <para>
/// Every fill sets the buffer's current length to the full frame (width * 4 * height, top-down rows of width * 4
/// bytes: the RGB32 input type declares that stride). Allocation-free per frame once the pool has grown; the staging
/// texture and the scaler are rebuilt only when the capture size changes. Not thread-safe: callers serialize (the
/// recorder's copy lock). Dispose after the sink writer was released.
/// </para>
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </summary>
internal sealed unsafe class CpuFrameCopier : IFramePath
{
    private readonly nint _device;
    private readonly nint _context;
    private readonly PixelSize _encoder;
    private readonly int _stride;
    private readonly uint _frameBytes;
    private readonly MemorySamplePool _pool;
    private nint _staging;
    private D3D11Texture2DDesc _stagingDesc;
    private BgraScaler? _scaler;
    private PixelSize _lastContent;
    private PixelSize _lastTexture;

    /// <exception cref="COMException">The initial samples could not be created.</exception>
    public CpuFrameCopier(nint device, nint context, PixelSize encoder, IMemorySampleFactory factory, int initialSamples, int maximumSamples)
    {
        _device = device;
        _context = context;
        _encoder = encoder;
        _stride = Stride(encoder);
        _frameBytes = FrameBytes(encoder);
        _pool = new MemorySamplePool(factory, _frameBytes, initialSamples, maximumSamples);
    }

    public EncoderInput Input => EncoderInput.Cpu;

    public FrameFailure LastFailure { get; private set; }

    /// <summary>System-memory samples created so far (diagnostics).</summary>
    public int PooledSamples => _pool.Count;

    /// <summary>Row pitch of the encoder frame in bytes (MF_MT_DEFAULT_STRIDE of the RGB32 input: positive = top-down).</summary>
    public static int Stride(PixelSize encoder) => checked((int)PlaceholderFrame.RowPitch(encoder.Width));

    /// <summary>Bytes of one encoder frame (the buffers' current length).</summary>
    public static uint FrameBytes(PixelSize encoder)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(encoder.Height, nameof(encoder));
        return checked(PlaceholderFrame.RowPitch(encoder.Width) * (uint)encoder.Height);
    }

    /// <summary>True for the single-plane 32-bit BGRA formats a capture texture can have.</summary>
    public static bool IsBgra32(uint format) => format is ScreenConstants.DxgiFormatB8G8R8A8Unorm
        or ScreenConstants.DxgiFormatB8G8R8X8Unorm
        or ScreenConstants.DxgiFormatB8G8R8A8UnormSrgb
        or ScreenConstants.DxgiFormatB8G8R8X8UnormSrgb;

    public int AllocateSample(out nint sample)
    {
        var hr = _pool.TryAcquire(out sample);
        if (hr < 0 && hr != ScreenConstants.MfESampleAllocatorEmpty)
        {
            LastFailure = new FrameFailure(FrameStep.SampleCreate, hr);
        }

        return hr;
    }

    public int CopyFrame(nint sample, nint source, PixelSize content)
    {
        if (!_pool.TryGetBuffer(sample, out var buffer))
        {
            return Fail(FrameStep.SampleCache, ScreenConstants.EInvalidArg);
        }

        D3D11Texture2DDesc desc;
        ID3D11Texture2D.GetDesc(source, &desc);
        if (!IsBgra32(desc.Format) || desc.SampleDesc.Count != 1 || desc.Width == 0 || desc.Height == 0)
        {
            return Fail(FrameStep.SourceFormat, ScreenConstants.EInvalidArg);
        }

        var texture = new PixelSize((int)desc.Width, (int)desc.Height);
        if (_scaler is null || content != _lastContent || texture != _lastTexture)
        {
            _scaler = new BgraScaler(EncoderGeometry.Place(content, texture, _encoder), _encoder); // geometry changes only
            _lastContent = content;
            _lastTexture = texture;
        }

        var hr = EnsureStaging(in desc);
        if (hr < 0)
        {
            return Fail(FrameStep.StagingTexture, hr);
        }

        // Only the region the scaler reads; the staging texture has the capture texture's size and format.
        var extent = _scaler.SourceExtent;
        var box = new D3D11Box { Right = (uint)extent.Width, Bottom = (uint)extent.Height, Back = 1 };
        ID3D11DeviceContext.CopySubresourceRegion(_context, _staging, 0, 0, 0, 0, source, 0, &box);

        D3D11MappedSubresource mapped;
        hr = ID3D11DeviceContext.Map(_context, _staging, 0, ScreenConstants.D3D11MapRead, 0, &mapped);
        if (hr < 0)
        {
            return Fail(FrameStep.MapStaging, hr);
        }

        try
        {
            if (mapped.Data == null || mapped.RowPitch < (uint)extent.Width * 4)
            {
                return Fail(FrameStep.MapStaging, ScreenConstants.EPointer);
            }

            byte* destination;
            hr = Lock(buffer, out destination);
            if (hr < 0)
            {
                return hr;
            }

            try
            {
                _scaler.Convert((byte*)mapped.Data, (int)mapped.RowPitch, destination, _stride);
            }
            finally
            {
                IMFMediaBuffer.Unlock(buffer);
            }
        }
        finally
        {
            ID3D11DeviceContext.Unmap(_context, _staging, 0);
        }

        return Commit(buffer);
    }

    public int CopySample(nint destinationSample, nint sourceSample)
    {
        if (!_pool.TryGetBuffer(destinationSample, out var destinationBuffer) || !_pool.TryGetBuffer(sourceSample, out var sourceBuffer))
        {
            return Fail(FrameStep.SampleCache, ScreenConstants.EInvalidArg);
        }

        var hr = Lock(sourceBuffer, out var source);
        if (hr < 0)
        {
            return hr;
        }

        try
        {
            hr = Lock(destinationBuffer, out var destination);
            if (hr < 0)
            {
                return hr;
            }

            try
            {
                new ReadOnlySpan<byte>(source, (int)_frameBytes).CopyTo(new Span<byte>(destination, (int)_frameBytes));
            }
            finally
            {
                IMFMediaBuffer.Unlock(destinationBuffer);
            }
        }
        finally
        {
            IMFMediaBuffer.Unlock(sourceBuffer);
        }

        return Commit(destinationBuffer);
    }

    public int FillBlack(nint sample)
    {
        if (!_pool.TryGetBuffer(sample, out var buffer))
        {
            return Fail(FrameStep.SampleCache, ScreenConstants.EInvalidArg);
        }

        var hr = Lock(buffer, out var destination);
        if (hr < 0)
        {
            return hr;
        }

        try
        {
            new Span<uint>(destination, (int)(_frameBytes / sizeof(uint))).Fill(PlaceholderFrame.OpaqueBlackBgra);
        }
        finally
        {
            IMFMediaBuffer.Unlock(buffer);
        }

        return Commit(buffer);
    }

    public void Dispose()
    {
        ComCall.Release(ref _staging);
        _pool.Dispose();
    }

    /// <summary>Locks a pool buffer for writing; fails when it is smaller than one frame.</summary>
    private int Lock(nint buffer, out byte* data)
    {
        byte* pointer = null;
        uint max = 0, current = 0;
        var hr = IMFMediaBuffer.Lock(buffer, &pointer, &max, &current);
        if (hr < 0 || pointer == null)
        {
            data = null;
            return Fail(FrameStep.LockBuffer, hr < 0 ? hr : ScreenConstants.EPointer);
        }

        if (max < _frameBytes)
        {
            IMFMediaBuffer.Unlock(buffer);
            data = null;
            return Fail(FrameStep.BufferTooSmall, ScreenConstants.EInvalidArg);
        }

        data = pointer;
        return ScreenConstants.SOk;
    }

    /// <summary>Marks the whole frame as valid data (IMFMediaBuffer::SetCurrentLength).</summary>
    private int Commit(nint buffer)
    {
        var hr = IMFMediaBuffer.SetCurrentLength(buffer, _frameBytes);
        return hr < 0 ? Fail(FrameStep.BufferLength, hr) : ScreenConstants.SOk;
    }

    private int EnsureStaging(in D3D11Texture2DDesc source)
    {
        if (_staging != 0 && _stagingDesc.Width == source.Width && _stagingDesc.Height == source.Height && _stagingDesc.Format == source.Format)
        {
            return ScreenConstants.SOk;
        }

        ComCall.Release(ref _staging);
        var desc = new D3D11Texture2DDesc
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = source.Format,
            SampleDesc = new DxgiSampleDesc { Count = 1 },
            Usage = ScreenConstants.D3D11UsageStaging,
            BindFlags = 0,
            CpuAccessFlags = ScreenConstants.D3D11CpuAccessRead,
            MiscFlags = 0,
        };
        nint staging = 0;
        var hr = ID3D11Device.CreateTexture2D(_device, &desc, null, &staging);
        if (hr < 0)
        {
            return hr;
        }

        _staging = staging;
        _stagingDesc = desc;
        return ScreenConstants.SOk;
    }

    private int Fail(FrameStep step, int hr)
    {
        LastFailure = new FrameFailure(step, hr);
        return hr;
    }
}
