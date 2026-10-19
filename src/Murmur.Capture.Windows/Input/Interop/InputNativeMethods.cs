using System.Runtime.InteropServices;

namespace Murmur.Capture.Windows.Input.Interop;

/// <summary>
/// Flat Win32 imports of the global-hotkey window (user32, kernel32), all source-generated with
/// <see cref="LibraryImportAttribute"/>. Signatures follow the Windows SDK 10.0.26100 headers.
/// </summary>
internal static unsafe partial class InputNativeMethods
{
    /// <summary><c>HMODULE GetModuleHandleW(LPCWSTR lpModuleName)</c>; null = the exe.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GetModuleHandleW(char* moduleName);

    /// <summary><c>ATOM RegisterClassExW(const WNDCLASSEXW *)</c>; 0 on failure.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial ushort RegisterClassExW(WndClassExW* windowClass);

    /// <summary>
    /// <c>HWND CreateWindowExW(DWORD dwExStyle, LPCWSTR lpClassName, LPCWSTR lpWindowName, DWORD dwStyle, int X, int Y,
    /// int nWidth, int nHeight, HWND hWndParent, HMENU hMenu, HINSTANCE hInstance, LPVOID lpParam)</c>; 0 on failure.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint CreateWindowExW(
        uint exStyle,
        char* className,
        char* windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    /// <summary><c>BOOL DestroyWindow(HWND)</c>; must run on the thread that created the window.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int DestroyWindow(nint window);

    /// <summary><c>LRESULT DefWindowProcW(HWND, UINT, WPARAM, LPARAM)</c>.</summary>
    [LibraryImport("user32.dll")]
    public static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    /// <summary><c>BOOL GetMessageW(LPMSG, HWND, UINT, UINT)</c>: &gt; 0 message, 0 WM_QUIT, -1 error.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int GetMessageW(Msg* message, nint window, uint filterMin, uint filterMax);

    /// <summary><c>LRESULT DispatchMessageW(const MSG *)</c>.</summary>
    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(Msg* message);

    /// <summary><c>BOOL PostMessageW(HWND, UINT, WPARAM, LPARAM)</c>; callable from any thread.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int PostMessageW(nint window, uint message, nint wParam, nint lParam);

    /// <summary><c>void PostQuitMessage(int nExitCode)</c>; posts WM_QUIT to the calling thread.</summary>
    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int exitCode);

    /// <summary><c>BOOL RegisterHotKey(HWND, int id, UINT fsModifiers, UINT vk)</c>; binds to the calling thread.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    /// <summary><c>BOOL UnregisterHotKey(HWND, int id)</c>; must run on the thread that registered.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int UnregisterHotKey(nint window, int id);

    /// <summary><c>LONG_PTR SetWindowLongPtrW(HWND, int nIndex, LONG_PTR)</c> (x64 / arm64 export).</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    /// <summary><c>LONG_PTR GetWindowLongPtrW(HWND, int nIndex)</c>.</summary>
    [LibraryImport("user32.dll")]
    public static partial nint GetWindowLongPtrW(nint window, int index);
}
