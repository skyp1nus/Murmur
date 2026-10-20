using System.Runtime.InteropServices;

namespace Murmur.Capture.Windows.Screen.Interop;

/// <summary>
/// Flat Win32 imports for screen capture (user32, d3d11, mfplat, mfreadwrite, kernel32), all source-generated with
/// <see cref="LibraryImportAttribute"/>. Interface pointers are passed as raw <c>nint</c>; the caller owns the
/// returned references (see <see cref="ComCall"/>). Signatures follow the Windows SDK 10.0.26100 headers.
/// </summary>
internal static unsafe partial class ScreenNativeMethods
{
    // ---- user32.dll (winuser.h) ----

    /// <summary><c>BOOL EnumDisplayMonitors(HDC hdc, LPCRECT lprcClip, MONITORENUMPROC lpfnEnum, LPARAM dwData)</c>.</summary>
    [LibraryImport("user32.dll")]
    public static partial int EnumDisplayMonitors(nint hdc, Rect32* clip, delegate* unmanaged<nint, nint, Rect32*, nint, int> callback, nint data);

    /// <summary><c>BOOL GetMonitorInfoW(HMONITOR hMonitor, LPMONITORINFO lpmi)</c> with a MONITORINFOEXW.</summary>
    [LibraryImport("user32.dll")]
    public static partial int GetMonitorInfoW(nint monitor, MonitorInfoExW* info);

    /// <summary><c>BOOL EnumDisplaySettingsW(LPCWSTR lpszDeviceName, DWORD iModeNum, DEVMODEW *lpDevMode)</c>.</summary>
    [LibraryImport("user32.dll")]
    public static partial int EnumDisplaySettingsW(char* deviceName, uint modeNumber, DevModeW* mode);

    /// <summary><c>BOOL EnumDisplayDevicesW(LPCWSTR lpDevice, DWORD iDevNum, PDISPLAY_DEVICEW lpDisplayDevice, DWORD dwFlags)</c>.</summary>
    [LibraryImport("user32.dll")]
    public static partial int EnumDisplayDevicesW(char* device, uint deviceIndex, DisplayDeviceW* displayDevice, uint flags);

    // ---- d3d11.dll (d3d11.h, windows.graphics.directx.direct3d11.interop.h) ----

    /// <summary>
    /// <c>HRESULT D3D11CreateDevice(IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE Software, UINT Flags,
    /// const D3D_FEATURE_LEVEL*, UINT FeatureLevels, UINT SDKVersion, ID3D11Device**, D3D_FEATURE_LEVEL*,
    /// ID3D11DeviceContext**)</c>.
    /// </summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("d3d11.dll")]
    public static partial int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        int* featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        nint* device,
        int* featureLevel,
        nint* immediateContext);

    /// <summary><c>HRESULT CreateDirect3D11DeviceFromDXGIDevice(IDXGIDevice*, IInspectable**)</c>.</summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("d3d11.dll")]
    public static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, nint* graphicsDevice);

    // ---- mfplat.dll (mfapi.h) ----

    /// <summary><c>HRESULT MFStartup(ULONG Version, DWORD dwFlags)</c>.</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFStartup(uint version, uint flags);

    /// <summary><c>HRESULT MFCreateAttributes(IMFAttributes **ppMFAttributes, UINT32 cInitialSize)</c>.</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateAttributes(nint* attributes, uint initialSize);

    /// <summary><c>HRESULT MFCreateMediaType(IMFMediaType **ppMFType)</c>.</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMediaType(nint* mediaType);

    /// <summary><c>HRESULT MFCreateDXGIDeviceManager(UINT *resetToken, IMFDXGIDeviceManager **ppDeviceManager)</c>.</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateDXGIDeviceManager(uint* resetToken, nint* deviceManager);

    /// <summary><c>HRESULT MFCreateVideoSampleAllocatorEx(REFIID riid, void **ppSampleAllocator)</c>.</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateVideoSampleAllocatorEx(Guid* iid, nint* allocator);

    /// <summary><c>HRESULT MFCreateSample(IMFSample **ppIMFSample)</c>.</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateSample(nint* sample);

    /// <summary><c>HRESULT MFCreateMemoryBuffer(DWORD cbMaxLength, IMFMediaBuffer **ppBuffer)</c>.</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMemoryBuffer(uint maxLength, nint* buffer);

    /// <summary>
    /// <c>HRESULT MFTEnumEx(GUID guidCategory, UINT32 Flags, const MFT_REGISTER_TYPE_INFO *pInputType,
    /// const MFT_REGISTER_TYPE_INFO *pOutputType, IMFActivate ***pppMFTActivate, UINT32 *pnumMFTActivate)</c>. The
    /// category GUID is passed by value (the runtime applies the platform ABI); the array is freed with CoTaskMemFree.
    /// </summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFTEnumEx(Guid category, uint flags, MftRegisterTypeInfo* inputType, MftRegisterTypeInfo* outputType, nint** activates, uint* count);

    // ---- mfreadwrite.dll (mfreadwrite.h) ----

    /// <summary><c>HRESULT MFCreateSinkWriterFromURL(LPCWSTR pwszOutputURL, IMFByteStream*, IMFAttributes*, IMFSinkWriter**)</c>.</summary>
    [LibraryImport("mfreadwrite.dll")]
    public static partial int MFCreateSinkWriterFromURL(char* outputUrl, nint byteStream, nint attributes, nint* sinkWriter);

    /// <summary><c>HRESULT MFCreateSourceReaderFromURL(LPCWSTR pwszURL, IMFAttributes*, IMFSourceReader**)</c>.</summary>
    [LibraryImport("mfreadwrite.dll")]
    public static partial int MFCreateSourceReaderFromURL(char* url, nint attributes, nint* sourceReader);

    // ---- ole32.dll ----

    /// <summary><c>void CoTaskMemFree(LPVOID pv)</c>.</summary>
    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint memory);

    // ---- oleaut32.dll ----

    /// <summary><c>HRESULT VariantClear(VARIANTARG *pvarg)</c>: frees what a VARIANT owns and sets it to VT_EMPTY.</summary>
    [LibraryImport("oleaut32.dll")]
    public static partial int VariantClear(Variant* value);

    // ---- kernel32.dll ----

    /// <summary><c>BOOL ProcessIdToSessionId(DWORD dwProcessId, DWORD *pSessionId)</c>.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial int ProcessIdToSessionId(uint processId, uint* sessionId);
}
