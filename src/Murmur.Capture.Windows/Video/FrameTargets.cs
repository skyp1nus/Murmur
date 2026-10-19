using Murmur.Capture.Windows.Screen.Interop;
using Murmur.Capture.Windows.Video.Interop;

namespace Murmur.Capture.Windows.Video;

/// <summary>
/// The surface IMFMediaEngine::TransferVideoFrame draws into, and the read-back into the caller's buffer. Created for one
/// size and recreated only when the requested size changes. UI thread only; allocation-free after <see cref="Resize"/>.
/// </summary>
internal interface IFrameTarget : IDisposable
{
    /// <summary>The IUnknown handed to TransferVideoFrame (borrowed; 0 before <see cref="Resize"/>).</summary>
    nint Surface { get; }

    int Width { get; }

    int Height { get; }

    /// <summary>(Re)creates the surface for <paramref name="width"/> x <paramref name="height"/> when the size differs; an HRESULT.</summary>
    int Resize(int width, int height);

    /// <summary>Copies the surface into <paramref name="destination"/> (rows of <paramref name="stride"/> bytes); an HRESULT.</summary>
    int CopyTo(Span<byte> destination, int stride);
}

/// <summary>
/// D3D11 output: a BGRA render target the engine draws into (on the GPU, scaled), copied into a staging texture and mapped
/// for the CPU copy. The device's immediate context is multithread-protected (<c>GpuDevice</c>), as the engine uses the
/// same device on its own threads.
/// </summary>
internal sealed unsafe class D3D11FrameTarget(nint device, nint context) : IFrameTarget
{
    private nint _renderTarget;
    private nint _staging;

    public nint Surface => _renderTarget;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Resize(int width, int height)
    {
        if (width == Width && height == Height && _renderTarget != 0)
        {
            return 0;
        }

        Release();
        var desc = new D3D11Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = ScreenConstants.DxgiFormatB8G8R8A8Unorm,
            SampleDesc = new DxgiSampleDesc { Count = 1, Quality = 0 },
            Usage = ScreenConstants.D3D11UsageDefault,
            BindFlags = ScreenConstants.D3D11BindRenderTarget | ScreenConstants.D3D11BindShaderResource,
        };
        nint renderTarget = 0, staging = 0;
        var hr = ID3D11Device.CreateTexture2D(device, &desc, null, &renderTarget);
        if (hr >= 0)
        {
            desc.Usage = ScreenConstants.D3D11UsageStaging;
            desc.BindFlags = 0;
            desc.CpuAccessFlags = ScreenConstants.D3D11CpuAccessRead;
            hr = ID3D11Device.CreateTexture2D(device, &desc, null, &staging);
        }

        if (hr < 0)
        {
            ComCall.Release(ref staging);
            ComCall.Release(ref renderTarget);
            return hr;
        }

        _renderTarget = renderTarget;
        _staging = staging;
        Width = width;
        Height = height;
        return 0;
    }

    public int CopyTo(Span<byte> destination, int stride)
    {
        if (_renderTarget == 0)
        {
            return ScreenConstants.EPointer;
        }

        ID3D11DeviceContext.CopyResource(context, _staging, _renderTarget);
        D3D11MappedSubresource mapped;
        var hr = ID3D11DeviceContext.Map(context, _staging, 0, ScreenConstants.D3D11MapRead, 0, &mapped);
        if (hr < 0)
        {
            return hr;
        }

        try
        {
            BgraFrame.CopyRows((byte*)mapped.Data, (int)mapped.RowPitch, destination, Width, Height, stride);
        }
        finally
        {
            ID3D11DeviceContext.Unmap(context, _staging, 0);
        }

        return 0;
    }

    public void Dispose() => Release();

    private void Release()
    {
        ComCall.Release(ref _staging);
        ComCall.Release(ref _renderTarget);
        Width = 0;
        Height = 0;
    }
}

/// <summary>WIC output: a 32bpp BGRA WIC bitmap the engine draws into (software), locked for the copy.</summary>
internal sealed unsafe class WicFrameTarget(nint factory) : IFrameTarget
{
    private nint _bitmap;

    public nint Surface => _bitmap;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Resize(int width, int height)
    {
        if (width == Width && height == Height && _bitmap != 0)
        {
            return 0;
        }

        Release();
        var format = VideoConstants.WicPixelFormat32bppBgra;
        nint bitmap = 0;
        var hr = IWICImagingFactory.CreateBitmap(factory, (uint)width, (uint)height, &format, VideoConstants.WicBitmapCacheOnLoad, &bitmap);
        if (hr < 0)
        {
            return hr;
        }

        _bitmap = bitmap;
        Width = width;
        Height = height;
        return 0;
    }

    public int CopyTo(Span<byte> destination, int stride)
    {
        if (_bitmap == 0)
        {
            return ScreenConstants.EPointer;
        }

        var rect = new WICRect { X = 0, Y = 0, Width = Width, Height = Height };
        nint bitmapLock = 0;
        var hr = IWICBitmap.Lock(_bitmap, &rect, VideoConstants.WicBitmapLockRead, &bitmapLock);
        if (hr < 0)
        {
            return hr;
        }

        try
        {
            uint pitch = 0, size = 0;
            byte* data = null;
            hr = IWICBitmapLock.GetStride(bitmapLock, &pitch);
            if (hr >= 0)
            {
                hr = IWICBitmapLock.GetDataPointer(bitmapLock, &size, &data);
            }

            if (hr < 0)
            {
                return hr;
            }

            if ((long)pitch * (Height - 1) + ((long)Width * BgraFrame.BytesPerPixel) > size)
            {
                return ScreenConstants.EFail;
            }

            BgraFrame.CopyRows(data, (int)pitch, destination, Width, Height, stride);
            return 0;
        }
        finally
        {
            ComCall.Release(ref bitmapLock);
        }
    }

    public void Dispose() => Release();

    private void Release()
    {
        ComCall.Release(ref _bitmap);
        Width = 0;
        Height = 0;
    }
}
