using System.Runtime.InteropServices;

namespace Murmur.Capture.Windows.Audio.Interop;

/// <summary>Flat Win32 imports (ole32, avrt, kernel32), all source-generated with <see cref="LibraryImportAttribute"/>.</summary>
internal static unsafe partial class NativeMethods
{
    // ---- ole32.dll (combaseapi.h, objbase.h, propidl.h) ----

    /// <summary><c>HRESULT CoInitializeEx(LPVOID pvReserved, DWORD dwCoInit)</c>.</summary>
    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);

    /// <summary><c>void CoUninitialize(void)</c>.</summary>
    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    /// <summary><c>HRESULT CoCreateInstance(REFCLSID rclsid, LPUNKNOWN pUnkOuter, DWORD dwClsContext, REFIID riid, LPVOID *ppv)</c>.</summary>
    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint clsContext, in Guid iid, out nint instance);

    /// <summary><c>void CoTaskMemFree(LPVOID pv)</c>.</summary>
    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint memory);

    /// <summary><c>HRESULT PropVariantClear(PROPVARIANT *pvar)</c>.</summary>
    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(ref PropVariant value);

    // ---- avrt.dll (avrt.h) ----

    /// <summary><c>HANDLE AvSetMmThreadCharacteristicsW(LPCWSTR TaskName, LPDWORD TaskIndex)</c>; 0 on failure.</summary>
    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    /// <summary><c>BOOL AvRevertMmThreadCharacteristics(HANDLE AvrtHandle)</c>.</summary>
    [LibraryImport("avrt.dll")]
    public static partial int AvRevertMmThreadCharacteristics(nint avrtHandle);

    // ---- kernel32.dll (synchapi.h, handleapi.h, profileapi.h) ----

    /// <summary><c>HANDLE CreateEventW(LPSECURITY_ATTRIBUTES, BOOL bManualReset, BOOL bInitialState, LPCWSTR lpName)</c>; 0 on failure.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateEventW(nint securityAttributes, int manualReset, int initialState, nint name);

    /// <summary><c>BOOL SetEvent(HANDLE hEvent)</c>.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int SetEvent(nint handle);

    /// <summary><c>DWORD WaitForMultipleObjects(DWORD nCount, const HANDLE *lpHandles, BOOL bWaitAll, DWORD dwMilliseconds)</c>.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds);

    /// <summary><c>BOOL CloseHandle(HANDLE hObject)</c>.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int CloseHandle(nint handle);

    /// <summary><c>BOOL QueryPerformanceCounter(LARGE_INTEGER *lpPerformanceCount)</c>.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial int QueryPerformanceCounter(out long count);

    /// <summary><c>BOOL QueryPerformanceFrequency(LARGE_INTEGER *lpFrequency)</c>.</summary>
    [LibraryImport("kernel32.dll")]
    public static partial int QueryPerformanceFrequency(out long frequency);
}
