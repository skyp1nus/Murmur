using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Screen;

/// <summary>
/// Moves captured frames into encoder samples entirely on the GPU (no CPU readback): a sub-rectangle
/// <c>CopySubresourceRegion</c> when the capture already has the encoder size (up to the even-size crop), otherwise a
/// D3D11 video-processor blit that scales and letterboxes the frame (also used when the capture size changes during a
/// segment, since the encoder size is fixed per segment).
/// <para>
/// Samples come from the segment's <c>IMFVideoSampleAllocatorEx</c>, which recycles a fixed set of sample objects for
/// its whole lifetime; their textures (and video-processor output views) are cached per sample pointer, so the
/// steady-state path makes no COM allocations and no managed allocations. The cache holds texture references but never
/// a sample reference (that would keep the sample checked out of the allocator). Must be disposed before the allocator
/// is released. Not thread-safe: callers serialize (the recorder's copy lock).
/// </para>
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </summary>
internal sealed unsafe class GpuFrameCopier : IDisposable
{
    private const int MaxEntries = 32;

    private readonly nint _device;
    private readonly nint _context;
    private readonly PixelSize _encoder;
    private readonly int _frameRate;
    private readonly Entry[] _entries = new Entry[MaxEntries];
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

    public GpuFrameCopier(nint device, nint context, PixelSize encoder, int frameRate)
    {
        _device = device;
        _context = context;
        _encoder = encoder;
        _frameRate = frameRate;
    }

    /// <summary>Frames that went through the video processor (diagnostics).</summary>
    public long ScaledFrames { get; private set; }

    /// <summary>
    /// Copies the capture texture <paramref name="source"/> (B8G8R8A8, valid region <paramref name="content"/>) into
    /// the texture behind <paramref name="sample"/>. Returns an HRESULT.
    /// </summary>
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
            return ScreenConstants.SOk;
        }

        return Blit(ref entry, source, texture);
    }

    /// <summary>Copies the whole picture of <paramref name="sourceSample"/> into <paramref name="destinationSample"/> (frame repeat).</summary>
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
        return ScreenConstants.SOk;
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
                return hr;
            }

            entry.OutputView = view;
        }

        // The capture texture belongs to the frame pool: copy it into a texture we own (known bind flags) first.
        ID3D11DeviceContext.CopySubresourceRegion(_context, _intermediate, 0, 0, 0, 0, source, 0, null);
        var stream = new D3D11VideoProcessorStream { Enable = 1, InputSurface = _inputView };
        hr = ID3D11VideoContext.VideoProcessorBlt(_videoContext, _processor, entry.OutputView, 0, 1, &stream);
        if (hr >= 0)
        {
            ScaledFrames++;
        }

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
        _videoContext = ComCall.Query(_context, ScreenConstants.IidID3D11VideoContext);
        if (_videoDevice == 0 || _videoContext == 0)
        {
            return unchecked((int)0x80004002); // E_NOINTERFACE: device created without D3D11_CREATE_DEVICE_VIDEO_SUPPORT
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
            return hr;
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
            return hr;
        }

        _enumerator = enumerator;
        hr = ID3D11VideoDevice.CreateVideoProcessor(_videoDevice, _enumerator, 0, &processor);
        if (hr < 0)
        {
            return hr;
        }

        _processor = processor;
        var inputDesc = new D3D11VideoProcessorInputViewDesc { ViewDimension = ScreenConstants.D3D11VpivDimensionTexture2D };
        hr = ID3D11VideoDevice.CreateVideoProcessorInputView(_videoDevice, _intermediate, _enumerator, &inputDesc, &inputView);
        if (hr < 0)
        {
            return hr;
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
            return ScreenConstants.EFail;
        }

        nint buffer = 0, dxgiBuffer = 0, texture = 0;
        try
        {
            var hr = IMFSample.GetBufferByIndex(sample, 0, &buffer);
            if (hr < 0)
            {
                return hr;
            }

            dxgiBuffer = ComCall.Query(buffer, ScreenConstants.IidIMFDXGIBuffer);
            if (dxgiBuffer == 0)
            {
                return unchecked((int)0x80004002); // E_NOINTERFACE: not a DXGI sample
            }

            var iid = ScreenConstants.IidID3D11Texture2D;
            hr = IMFDXGIBuffer.GetResource(dxgiBuffer, &iid, &texture);
            if (hr < 0)
            {
                return hr;
            }

            uint subresource;
            hr = IMFDXGIBuffer.GetSubresourceIndex(dxgiBuffer, &subresource);
            if (hr < 0)
            {
                return hr;
            }

            D3D11Texture2DDesc desc;
            ID3D11Texture2D.GetDesc(texture, &desc);
            index = _entryCount++;
            _entries[index] = new Entry
            {
                Sample = sample,
                Texture = texture,
                Subresource = subresource,
                ArraySize = desc.ArraySize,
                CanCopy = desc.Format == ScreenConstants.DxgiFormatB8G8R8A8Unorm && desc.SampleDesc.Count == 1,
            };
            texture = 0; // owned by the entry now
            return ScreenConstants.SOk;
        }
        finally
        {
            ComCall.Release(ref texture);
            ComCall.Release(ref dxgiBuffer);
            ComCall.Release(ref buffer);
        }
    }

    private static Rect32 ToRect(PixelRect r) => new(r.Left, r.Top, r.Right, r.Bottom);

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
            _entries[i] = default;
        }

        _entryCount = 0;
    }

    private struct Entry
    {
        public nint Sample;
        public nint Texture;
        public uint Subresource;
        public uint ArraySize;
        public bool CanCopy;
        public nint OutputView;
    }
}
