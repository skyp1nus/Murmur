using System.Runtime.InteropServices;

namespace Murmur.Capture.Windows.Screen.Interop;

// Blittable mirrors of the Win32 / D3D11 structs used by screen capture. Layouts follow the Windows SDK 10.0.26100
// headers (cross-checked against Microsoft.Windows.SDK.Win32Metadata 71.0.30-preview); sizes and offsets are asserted
// by ScreenInteropLayoutTests.

/// <summary>windef.h <c>RECT</c> (16 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Rect32
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public Rect32(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }
}

/// <summary>winuser.h <c>MONITORINFOEXW</c> (104 bytes): MONITORINFO + <c>WCHAR szDevice[CCHDEVICENAME = 32]</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MonitorInfoExW
{
    public const int DeviceNameChars = 32;

    public uint Size;
    public Rect32 Monitor;
    public Rect32 Work;
    public uint Flags;
    public fixed char Device[DeviceNameChars];
}

/// <summary>
/// wingdi.h <c>DEVMODEW</c> (220 bytes). Only the display fields read here are named; the rest is padding at the
/// header offsets (printer/display unions included).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 220)]
internal struct DevModeW
{
    public const int NativeSize = 220;

    [FieldOffset(68)]
    public ushort Size;

    [FieldOffset(70)]
    public ushort DriverExtra;

    [FieldOffset(72)]
    public uint Fields;

    [FieldOffset(76)]
    public int PositionX;

    [FieldOffset(80)]
    public int PositionY;

    [FieldOffset(168)]
    public uint BitsPerPel;

    [FieldOffset(172)]
    public uint PelsWidth;

    [FieldOffset(176)]
    public uint PelsHeight;

    [FieldOffset(184)]
    public uint DisplayFrequency;
}

/// <summary>wingdi.h <c>DISPLAY_DEVICEW</c> (840 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DisplayDeviceW
{
    public uint Size;
    public fixed char DeviceName[32];
    public fixed char DeviceString[128];
    public uint StateFlags;
    public fixed char DeviceId[128];
    public fixed char DeviceKey[128];
}

/// <summary>Windows.Graphics.SizeInt32 as it crosses the WinRT ABI (8 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SizeInt32Abi
{
    public int Width;
    public int Height;
}

/// <summary>dxgicommon.h <c>DXGI_SAMPLE_DESC</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DxgiSampleDesc
{
    public uint Count;
    public uint Quality;
}

/// <summary>d3d11.h <c>D3D11_TEXTURE2D_DESC</c> (44 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DDesc
{
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ArraySize;
    public uint Format;
    public DxgiSampleDesc SampleDesc;
    public uint Usage;
    public uint BindFlags;
    public uint CpuAccessFlags;
    public uint MiscFlags;
}

/// <summary>d3d11.h <c>D3D11_SUBRESOURCE_DATA</c> (16 bytes on 64-bit, 12 on 32-bit).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct D3D11SubresourceData
{
    public void* SysMem;
    public uint SysMemPitch;
    public uint SysMemSlicePitch;
}

/// <summary>d3d11.h <c>D3D11_MAPPED_SUBRESOURCE</c> (16 bytes on 64-bit, 12 on 32-bit).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct D3D11MappedSubresource
{
    public void* Data;
    public uint RowPitch;
    public uint DepthPitch;
}

/// <summary>
/// dxgi.h <c>DXGI_ADAPTER_DESC</c> (304 bytes on 64-bit, 292 on 32-bit): <c>WCHAR Description[128]</c>, vendor / device
/// / subsystem / revision ids, three <c>SIZE_T</c> memory sizes and the adapter <c>LUID</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DxgiAdapterDesc
{
    public const int DescriptionChars = 128;

    public fixed char Description[DescriptionChars];
    public uint VendorId;
    public uint DeviceId;
    public uint SubSysId;
    public uint Revision;
    public nuint DedicatedVideoMemory;
    public nuint DedicatedSystemMemory;
    public nuint SharedSystemMemory;
    public uint AdapterLuidLowPart;
    public int AdapterLuidHighPart;
}

/// <summary>mfobjects.h <c>MFT_REGISTER_TYPE_INFO</c> (32 bytes): major type and subtype GUIDs.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftRegisterTypeInfo
{
    public Guid MajorType;
    public Guid Subtype;
}

/// <summary>d3d11.h <c>D3D11_BOX</c> (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Box
{
    public uint Left;
    public uint Top;
    public uint Front;
    public uint Right;
    public uint Bottom;
    public uint Back;
}

/// <summary>dxgicommon.h <c>DXGI_RATIONAL</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DxgiRational
{
    public uint Numerator;
    public uint Denominator;
}

/// <summary>d3d11.h <c>D3D11_VIDEO_PROCESSOR_CONTENT_DESC</c> (40 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11VideoProcessorContentDesc
{
    public uint InputFrameFormat;
    public DxgiRational InputFrameRate;
    public uint InputWidth;
    public uint InputHeight;
    public DxgiRational OutputFrameRate;
    public uint OutputWidth;
    public uint OutputHeight;
    public uint Usage;
}

/// <summary>
/// d3d11.h <c>D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC</c> (16 bytes): FourCC, ViewDimension, union { D3D11_TEX2D_VPIV
/// { MipSlice, ArraySlice } }.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11VideoProcessorInputViewDesc
{
    public uint FourCC;
    public uint ViewDimension;
    public uint MipSlice;
    public uint ArraySlice;
}

/// <summary>
/// d3d11.h <c>D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC</c> (16 bytes): ViewDimension, union { D3D11_TEX2D_VPOV { MipSlice },
/// D3D11_TEX2D_ARRAY_VPOV { MipSlice, FirstArraySlice, ArraySize } }.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11VideoProcessorOutputViewDesc
{
    public uint ViewDimension;
    public uint MipSlice;
    public uint FirstArraySlice;
    public uint ArraySize;
}

/// <summary>d3d11.h <c>D3D11_VIDEO_PROCESSOR_STREAM</c> (72 bytes on 64-bit).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11VideoProcessorStream
{
    public int Enable;
    public uint OutputIndex;
    public uint InputFrameOrField;
    public uint PastFrames;
    public uint FutureFrames;
    public nint PastSurfaces;
    public nint InputSurface;
    public nint FutureSurfaces;
    public nint PastSurfacesRight;
    public nint InputSurfaceRight;
    public nint FutureSurfacesRight;
}

/// <summary>d3d11.h <c>D3D11_VIDEO_COLOR</c> as its RGBA union arm (16 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11VideoColorRgba
{
    public float R;
    public float G;
    public float B;
    public float A;
}

/// <summary>
/// oaidl.h <c>VARIANT</c> for the scalar values ICodecAPI exchanges (VT_UI4 / VT_UI8 / VT_I4 / VT_BOOL): <c>vt</c> at 0,
/// the value union at 8. 24 bytes, the size of a VARIANT on 64-bit (16 on 32-bit, where the extra bytes are unused).
/// Holds no owned memory for these types; a value read back with ICodecAPI::GetValue is still cleared with
/// VariantClear, as an encoder may answer with a type that does.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct Variant
{
    [FieldOffset(0)]
    public ushort Vt;

    [FieldOffset(8)]
    public uint UI4;

    [FieldOffset(8)]
    public ulong UI8;

    public static Variant FromUInt32(uint value) => new() { Vt = ScreenConstants.VtUI4, UI4 = value };

    /// <summary>The value as a UINT32 when it is one (VT_UI4, VT_I4, VT_BOOL, or a VT_UI8 that fits), else null.</summary>
    public readonly uint? AsUInt32() => Vt switch
    {
        ScreenConstants.VtUI4 or ScreenConstants.VtI4 or ScreenConstants.VtUInt or ScreenConstants.VtInt => UI4,
        ScreenConstants.VtBool => UI4 & 0xFFFF,
        ScreenConstants.VtUI8 when UI8 <= uint.MaxValue => (uint)UI8,
        _ => null,
    };
}
