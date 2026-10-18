using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Screen;

/// <summary>
/// Zero-copy frame path (<see cref="EncoderInput.Gpu"/>): moves captured frames into encoder samples entirely on the
/// GPU (no CPU readback): a sub-rectangle <c>CopySubresourceRegion</c> when the capture already has the encoder size
/// (up to the even-size crop), otherwise a D3D11 video-processor blit that scales and letterboxes the frame (also used
/// when the capture size changes during a segment, since the encoder size is fixed per segment).
/// <para>
/// Samples come from the segment's <c>IMFVideoSampleAllocatorEx</c> (owned by this copier), which recycles a fixed set
/// of sample objects for its whole lifetime; their textures, media buffers (and video-processor output views) are
/// cached per sample pointer, so the steady-state path makes no COM allocations and no managed allocations. The cache
/// holds texture and buffer references but never a sample reference (that would keep the sample checked out of the
/// allocator). Every fill sets the buffer's current length to its contiguous length: buffers of DXGI surfaces start at
/// 0 bytes and <c>IMFSinkWriter::WriteSample</c> rejects them (E_INVALIDARG) on paths that read the buffer.
/// Not thread-safe: callers serialize (the recorder's copy lock). Dispose after the sink writer was released; disposing
/// uninitializes and releases the allocator.
/// </para>
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI on a machine with a hardware encoder.
/// </summary>
internal sealed unsafe class GpuFrameCopier : IFramePath
{
    private const int MaxEntries = 32;

    private readonly nint _device;
    private readonly nint _context;
    private readonly PixelSize _encoder;
    private readonly int _frameRate;
    private readonly uint _frameBytes;
    private readonly Entry[] _entries = new Entry[MaxEntries];
    private nint _allocator;
    private int _entryCount;

    private nint _videoDevice;
    private nint _videoContext;
    private nint _enumerator;
    private nint _processor;
    private nint _intermediate;
    private nint _inputView;
    private PixelSize _processorInput;

    private PixelSize _lastContent;
    private PixelSize _lastTexture;
    private FramePlacement _placement;
    private bool _placementValid;
    private bool _rectsApplied;

    /// <summary>Takes ownership of <paramref name="allocator"/> (an initialized IMFVideoSampleAllocatorEx; 0 for tests that never allocate).</summary>
    internal GpuFrameCopier(nint device, nint context, nint allocator, PixelSize encoder, int frameRate)
    {
        _device = device;
        _context = context;
        _allocator = allocator;
        _encoder = encoder;
        _frameRate = frameRate;
        _frameBytes = (uint)PlaceholderFrame.RowPitch(encoder.Width) * (uint)encoder.Height;
    }

    public EncoderInput Input => EncoderInput.Gpu;

    public FrameFailure LastFailure { get; private set; }

    /// <summary>Frames that went through the video processor (diagnostics).</summary>
    public long ScaledFrames { get; private set; }

    /// <summary>
    /// Creates the segment's sample allocator (D3D11 B8G8R8A8 render-target textures of <paramref name="encoder"/> size
    /// on the device behind <paramref name="deviceManager"/>) and the copier that owns it.
    /// </summary>
    /// <exception cref="COMException">The allocator could not be created or initialized.</exception>
    public static GpuFrameCopier Create(nint device, nint context, nint deviceManager, PixelSize encoder, int frameRate, uint initialSamples, uint maximumSamples)
    {
        var allocator = CreateSampleAllocator(deviceManager, encoder, frameRate, initialSamples, maximumSamples);
        return new GpuFrameCopier(device, context, allocator, encoder, frameRate);
    }

    public int AllocateSample(out nint sample)
    {
        nint result = 0;
        var hr = IMFVideoSampleAllocatorEx.AllocateSample(_allocator, &result);
        sample = hr < 0 ? 0 : result;
        return hr;
    }

    public int CopyFrame(nint sample, nint source, PixelSize content)
    {
        D3D11Texture2DDesc desc;
        ID3D11Texture2D.GetDesc(source, &desc);
        var texture = new PixelSize((int)desc.Width, (int)desc.Height);
        if (!_placementValid || content != _lastContent || texture != _lastTexture)
        {
            _placement = EncoderGeometry.Place(content, texture, _encoder);
            _lastContent = content;
            _lastTexture = texture;
            _placementValid = true;
            _rectsApplied = false;
        }

        var hr = GetEntry(sample, out var index);
        if (hr < 0)
        {
            return hr;
        }

        ref var entry = ref _entries[index];
        if (_placement.DirectCopy && entry.CanCopy && desc.Format == ScreenConstants.DxgiFormatB8G8R8A8Unorm)
        {
            var box = new D3D11Box
            {
                Right = (uint)_placement.Source.Right,
                Bottom = (uint)_placement.Source.Bottom,
                Back = 1,
            };
            ID3D11DeviceContext.CopySubresourceRegion(_context, entry.Texture, entry.Subresource, 0, 0, 0, source, 0, &box);
            return Commit(ref entry);
        }

        hr = Blit(ref entry, source, texture);
        return hr < 0 ? hr : Commit(ref entry);
    }

    public int CopySample(nint destinationSample, nint sourceSample)
    {
        var hr = GetEntry(destinationSample, out var destination);
        if (hr < 0)
        {
            return hr;
        }

        hr = GetEntry(sourceSample, out var source);
        if (hr < 0)
        {
            return hr;
        }

        ref var d = ref _entries[destination];
        ref var s = ref _entries[source];
        ID3D11DeviceContext.CopySubresourceRegion(_context, d.Texture, d.Subresource, 0, 0, 0, s.Texture, s.Subresource, null);
        return Commit(ref d);
    }

    /// <summary>
    /// Fills the picture of <paramref name="sample"/> with opaque black. Not a hot path: it creates a temporary
    /// encoder-size texture from zeroed CPU memory, copies it on the GPU and releases it again.
    /// </summary>
    public int FillBlack(nint sample)
    {
        var hr = GetEntry(sample, out var index);
        if (hr < 0)
        {
            return hr;
        }

        ref var entry = ref _entries[index];
        if (!entry.CanCopy)
        {
            return Fail(FrameStep.SampleFormat, ScreenConstants.EFail);
        }

        var pixels = PlaceholderFrame.PixelCount(_encoder);
        var memory = (uint*)NativeMemory.Alloc((nuint)pixels, sizeof(uint));
        nint black = 0;
        try
        {
            new Span<uint>(memory, checked((int)pixels)).Fill(PlaceholderFrame.OpaqueBlackBgra);
            var desc = new D3D11Texture2DDesc
            {
                Width = (uint)_encoder.Width,
                Height = (uint)_encoder.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = ScreenConstants.DxgiFormatB8G8R8A8Unorm,
                SampleDesc = new DxgiSampleDesc { Count = 1 },
                Usage = ScreenConstants.D3D11UsageDefault,
                BindFlags = ScreenConstants.D3D11BindShaderResource,
            };
            var data = new D3D11SubresourceData
            {
                SysMem = memory,
                SysMemPitch = PlaceholderFrame.RowPitch(_encoder.Width),
                SysMemSlicePitch = 0,
            };
            hr = ID3D11Device.CreateTexture2D(_device, &desc, &data, &black);
            if (hr < 0)
            {
                return Fail(FrameStep.BlackTexture, hr);
            }

            ID3D11DeviceContext.CopySubresourceRegion(_context, entry.Texture, entry.Subresource, 0, 0, 0, black, 0, null);
            return Commit(ref entry);
        }
        finally
        {
            ComCall.Release(ref black);
            NativeMemory.Free(memory);
        }
    }

    /// <summary>Marks the whole picture of a filled sample as valid data (IMFMediaBuffer::SetCurrentLength).</summary>
    private int Commit(ref Entry entry)
    {
        var hr = IMFMediaBuffer.SetCurrentLength(entry.Buffer, entry.Length);
        return hr < 0 ? Fail(FrameStep.BufferLength, hr) : ScreenConstants.SOk;
    }

    private int Fail(FrameStep step, int hr)
    {
        LastFailure = new FrameFailure(step, hr);
        return hr;
    }

    private int Blit(ref Entry entry, nint source, PixelSize texture)
    {
        var hr = EnsureProcessor(texture);
        if (hr < 0)
        {
            return hr;
        }

        if (!_rectsApplied)
        {
            var src = ToRect(_placement.Source);
            var dst = ToRect(_placement.Destination);
            var target = new Rect32(0, 0, _encoder.Width, _encoder.Height);
            ID3D11VideoContext.VideoProcessorSetStreamSourceRect(_videoContext, _processor, 0, 1, &src);
            ID3D11VideoContext.VideoProcessorSetStreamDestRect(_videoContext, _processor, 0, 1, &dst);
            ID3D11VideoContext.VideoProcessorSetOutputTargetRect(_videoContext, _processor, 1, &target);
            _rectsApplied = true;
        }

        if (entry.OutputView == 0)
        {
            var viewDesc = entry.ArraySize > 1
                ? new D3D11VideoProcessorOutputViewDesc { ViewDimension = ScreenConstants.D3D11VpovDimensionTexture2DArray, FirstArraySlice = entry.Subresource, ArraySize = 1 }
                : new D3D11VideoProcessorOutputViewDesc { ViewDimension = ScreenConstants.D3D11VpovDimensionTexture2D };
            nint view = 0;
            hr = ID3D11VideoDevice.CreateVideoProcessorOutputView(_videoDevice, entry.Texture, _enumerator, &viewDesc, &view);
            if (hr < 0)
            {
                return Fail(FrameStep.ProcessorOutputView, hr);
            }

            entry.OutputView = view;
        }

        // The capture texture belongs to the frame pool: copy it into a texture we own (known bind flags) first.
        ID3D11DeviceContext.CopySubresourceRegion(_context, _intermediate, 0, 0, 0, 0, source, 0, null);
        var stream = new D3D11VideoProcessorStream { Enable = 1, InputSurface = _inputView };
        hr = ID3D11VideoContext.VideoProcessorBlt(_videoContext, _processor, entry.OutputView, 0, 1, &stream);
        if (hr < 0)
        {
            return Fail(FrameStep.ProcessorBlt, hr);
        }

        ScaledFrames++;
        return hr;
    }

    private int EnsureProcessor(PixelSize input)
    {
        if (_processor != 0 && input == _processorInput)
        {
            return ScreenConstants.SOk;
        }

        ReleaseProcessor();
        _videoDevice = ComCall.Query(_device, ScreenConstants.IidID3D11VideoDevice);
        if (_videoDevice == 0)
        {
            return Fail(FrameStep.VideoDevice, ScreenConstants.ENoInterface); // device created without D3D11_CREATE_DEVICE_VIDEO_SUPPORT
        }

        _videoContext = ComCall.Query(_context, ScreenConstants.IidID3D11VideoContext);
        if (_videoContext == 0)
        {
            return Fail(FrameStep.VideoContext, ScreenConstants.ENoInterface);
        }

        var textureDesc = new D3D11Texture2DDesc
        {
            Width = (uint)input.Width,
            Height = (uint)input.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = ScreenConstants.DxgiFormatB8G8R8A8Unorm,
            SampleDesc = new DxgiSampleDesc { Count = 1 },
            Usage = ScreenConstants.D3D11UsageDefault,
            BindFlags = ScreenConstants.D3D11BindRenderTarget | ScreenConstants.D3D11BindShaderResource,
        };
        nint intermediate = 0;
        var hr = ID3D11Device.CreateTexture2D(_device, &textureDesc, null, &intermediate);
        if (hr < 0)
        {
            return Fail(FrameStep.ProcessorTexture, hr);
        }

        _intermediate = intermediate;
        var rate = new DxgiRational { Numerator = (uint)_frameRate, Denominator = 1 };
        var contentDesc = new D3D11VideoProcessorContentDesc
        {
            InputFrameFormat = ScreenConstants.D3D11VideoFrameFormatProgressive,
            InputFrameRate = rate,
            InputWidth = (uint)input.Width,
            InputHeight = (uint)input.Height,
            OutputFrameRate = rate,
            OutputWidth = (uint)_encoder.Width,
            OutputHeight = (uint)_encoder.Height,
            Usage = ScreenConstants.D3D11VideoUsagePlaybackNormal,
        };
        nint enumerator = 0, processor = 0, inputView = 0;
        hr = ID3D11VideoDevice.CreateVideoProcessorEnumerator(_videoDevice, &contentDesc, &enumerator);
        if (hr < 0)
        {
            return Fail(FrameStep.ProcessorEnumerator, hr);
        }

        _enumerator = enumerator;
        hr = ID3D11VideoDevice.CreateVideoProcessor(_videoDevice, _enumerator, 0, &processor);
        if (hr < 0)
        {
            return Fail(FrameStep.Processor, hr);
        }

        _processor = processor;
        var inputDesc = new D3D11VideoProcessorInputViewDesc { ViewDimension = ScreenConstants.D3D11VpivDimensionTexture2D };
        hr = ID3D11VideoDevice.CreateVideoProcessorInputView(_videoDevice, _intermediate, _enumerator, &inputDesc, &inputView);
        if (hr < 0)
        {
            return Fail(FrameStep.ProcessorInputView, hr);
        }

        _inputView = inputView;
        var black = new D3D11VideoColorRgba { A = 1 };
        ID3D11VideoContext.VideoProcessorSetStreamFrameFormat(_videoContext, _processor, 0, ScreenConstants.D3D11VideoFrameFormatProgressive);
        ID3D11VideoContext.VideoProcessorSetStreamAutoProcessingMode(_videoContext, _processor, 0, 0);
        ID3D11VideoContext.VideoProcessorSetOutputBackgroundColor(_videoContext, _processor, 0, &black);
        _processorInput = input;
        _rectsApplied = false;
        return ScreenConstants.SOk;
    }

    private int GetEntry(nint sample, out int index)
    {
        for (var i = 0; i < _entryCount; i++)
        {
            if (_entries[i].Sample == sample)
            {
                index = i;
                return ScreenConstants.SOk;
            }
        }

        index = -1;
        if (_entryCount == MaxEntries)
        {
            return Fail(FrameStep.SampleCache, ScreenConstants.EFail);
        }

        nint buffer = 0, dxgiBuffer = 0, texture = 0;
        try
        {
            var hr = IMFSample.GetBufferByIndex(sample, 0, &buffer);
            if (hr < 0 || buffer == 0)
            {
                return Fail(FrameStep.SampleBuffer, hr < 0 ? hr : ScreenConstants.EPointer);
            }

            dxgiBuffer = ComCall.Query(buffer, ScreenConstants.IidIMFDXGIBuffer);
            if (dxgiBuffer == 0)
            {
                return Fail(FrameStep.DxgiBuffer, ScreenConstants.ENoInterface); // not a DXGI sample
            }

            var iid = ScreenConstants.IidID3D11Texture2D;
            hr = IMFDXGIBuffer.GetResource(dxgiBuffer, &iid, &texture);
            if (hr < 0 || texture == 0)
            {
                return Fail(FrameStep.BufferTexture, hr < 0 ? hr : ScreenConstants.EPointer);
            }

            uint subresource;
            hr = IMFDXGIBuffer.GetSubresourceIndex(dxgiBuffer, &subresource);
            if (hr < 0)
            {
                return Fail(FrameStep.BufferSubresource, hr);
            }

            D3D11Texture2DDesc desc;
            ID3D11Texture2D.GetDesc(texture, &desc);
            index = _entryCount++;
            _entries[index] = new Entry
            {
                Sample = sample,
                Texture = texture,
                Buffer = buffer,
                Length = ContiguousLength(buffer, _frameBytes),
                Subresource = subresource,
                ArraySize = desc.ArraySize,
                CanCopy = desc.Format == ScreenConstants.DxgiFormatB8G8R8A8Unorm && desc.SampleDesc.Count == 1,
            };
            texture = 0; // owned by the entry now
            buffer = 0;
            return ScreenConstants.SOk;
        }
        finally
        {
            ComCall.Release(ref texture);
            ComCall.Release(ref dxgiBuffer);
            ComCall.Release(ref buffer);
        }
    }

    /// <summary>
    /// The byte length of the picture behind a DXGI media buffer: IMF2DBuffer::GetContiguousLength, else
    /// IMFMediaBuffer::GetMaxLength, else <paramref name="fallback"/> (width * 4 * height).
    /// </summary>
    internal static uint ContiguousLength(nint buffer, uint fallback)
    {
        uint length = 0;
        var twoD = ComCall.Query(buffer, ScreenConstants.IidIMF2DBuffer);
        if (twoD != 0)
        {
            if (IMF2DBuffer.GetContiguousLength(twoD, &length) < 0)
            {
                length = 0;
            }

            ComCall.Release(ref twoD);
        }

        if (length == 0 && IMFMediaBuffer.GetMaxLength(buffer, &length) < 0)
        {
            length = 0;
        }

        return length != 0 ? length : fallback;
    }

    private static Rect32 ToRect(PixelRect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    private static nint CreateSampleAllocator(nint deviceManager, PixelSize size, int frameRate, uint initialSamples, uint maximumSamples)
    {
        nint allocator = 0, attributes = 0, mediaType = 0;
        try
        {
            var iid = ScreenConstants.IidIMFVideoSampleAllocatorEx;
            ComHelpers.Check(ScreenNativeMethods.MFCreateVideoSampleAllocatorEx(&iid, &allocator), "MFCreateVideoSampleAllocatorEx");
            ComHelpers.Check(IMFVideoSampleAllocatorEx.SetDirectXManager(allocator, deviceManager), "IMFVideoSampleAllocator::SetDirectXManager");

            ComHelpers.Check(ScreenNativeMethods.MFCreateAttributes(&attributes, 1), "MFCreateAttributes");
            var bindKey = ScreenConstants.MfSaD3D11BindFlags;
            ComHelpers.Check(
                IMFAttributes.SetUINT32(attributes, &bindKey, ScreenConstants.D3D11BindRenderTarget | ScreenConstants.D3D11BindShaderResource),
                "IMFAttributes::SetUINT32(MF_SA_D3D11_BINDFLAGS)");

            ComHelpers.Check(ScreenNativeMethods.MFCreateMediaType(&mediaType), "MFCreateMediaType");
            var majorKey = ScreenConstants.MfMtMajorType;
            var major = ScreenConstants.MfMediaTypeVideo;
            var subtypeKey = ScreenConstants.MfMtSubtype;
            var subtype = ScreenConstants.MfVideoFormatArgb32;
            var sizeKey = ScreenConstants.MfMtFrameSize;
            var rateKey = ScreenConstants.MfMtFrameRate;
            ComHelpers.Check(IMFAttributes.SetGUID(mediaType, &majorKey, &major), "IMFAttributes::SetGUID(MF_MT_MAJOR_TYPE)");
            ComHelpers.Check(IMFAttributes.SetGUID(mediaType, &subtypeKey, &subtype), "IMFAttributes::SetGUID(MF_MT_SUBTYPE)");
            ComHelpers.Check(IMFAttributes.SetUINT64(mediaType, &sizeKey, EncoderGeometry.Pack((uint)size.Width, (uint)size.Height)), "IMFAttributes::SetUINT64(MF_MT_FRAME_SIZE)");
            ComHelpers.Check(IMFAttributes.SetUINT64(mediaType, &rateKey, EncoderGeometry.Pack((uint)frameRate, 1)), "IMFAttributes::SetUINT64(MF_MT_FRAME_RATE)");

            ComHelpers.Check(
                IMFVideoSampleAllocatorEx.InitializeSampleAllocatorEx(allocator, initialSamples, maximumSamples, attributes, mediaType),
                "IMFVideoSampleAllocatorEx::InitializeSampleAllocatorEx");
            var result = allocator;
            allocator = 0;
            return result;
        }
        finally
        {
            ComCall.Release(ref mediaType);
            ComCall.Release(ref attributes);
            ComCall.Release(ref allocator);
        }
    }

    private void ReleaseProcessor()
    {
        for (var i = 0; i < _entryCount; i++)
        {
            ComCall.Release(ref _entries[i].OutputView);
        }

        ComCall.Release(ref _inputView);
        ComCall.Release(ref _processor);
        ComCall.Release(ref _enumerator);
        ComCall.Release(ref _intermediate);
        ComCall.Release(ref _videoContext);
        ComCall.Release(ref _videoDevice);
        _processorInput = default;
    }

    public void Dispose()
    {
        ReleaseProcessor();
        for (var i = 0; i < _entryCount; i++)
        {
            ComCall.Release(ref _entries[i].Texture);
            ComCall.Release(ref _entries[i].Buffer);
            _entries[i] = default;
        }

        _entryCount = 0;
        if (_allocator != 0)
        {
            IMFVideoSampleAllocatorEx.UninitializeSampleAllocator(_allocator);
            ComCall.Release(ref _allocator);
        }
    }

    private struct Entry
    {
        public nint Sample;
        public nint Texture;
        public nint Buffer;
        public uint Length;
        public uint Subresource;
        public uint ArraySize;
        public bool CanCopy;
        public nint OutputView;
    }
}
