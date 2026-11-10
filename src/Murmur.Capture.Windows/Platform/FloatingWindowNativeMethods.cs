using System.Runtime.InteropServices;

namespace Murmur.Capture.Windows.Platform;

/// <summary>Win32 imports for the floating recorder orb's window (user32, gdi32), source-generated; values follow the Windows SDK 10.0.26100 headers.</summary>
internal static partial class FloatingWindowNativeMethods
{
    /// <summary><c>GWL_EXSTYLE</c>: the index of the extended window style.</summary>
    public const int GwlExStyle = -20;

    /// <summary><c>GWLP_WNDPROC</c>: the index of the window procedure.</summary>
    public const int GwlpWndProc = -4;

    /// <summary><c>WS_EX_NOACTIVATE</c>: a click on the window does not activate it.</summary>
    public const nint WsExNoActivate = 0x08000000;

    /// <summary><c>WS_EX_TOOLWINDOW</c>: a tool window; it is not in Alt+Tab or the taskbar.</summary>
    public const nint WsExToolWindow = 0x00000080;

    /// <summary><c>WS_EX_APPWINDOW</c>: forces a taskbar button and an Alt+Tab entry; cleared so the tool-window style wins.</summary>
    public const nint WsExAppWindow = 0x00040000;

    /// <summary><c>WS_EX_TRANSPARENT</c>: the window is skipped by hit testing, so clicks fall through it; never wanted on the orb.</summary>
    public const nint WsExTransparent = 0x00000020;

    /// <summary><c>WS_EX_LAYERED</c>: a layered window, which is not drawn and lets clicks through until it gets its layer content; never wanted on the orb.</summary>
    public const nint WsExLayered = 0x00080000;

    /// <summary><c>DWMWA_CLOAK</c>: the app cloaks (hides from the screen and from hit testing) or uncloaks its window.</summary>
    public const int DwmwaCloak = 13;

    /// <summary><c>DWMWA_CLOAKED</c>: why the window is cloaked (<c>DWM_CLOAKED_APP</c> 1, <c>_SHELL</c> 2, <c>_INHERITED</c> 4), 0 when it is not.</summary>
    public const int DwmwaCloaked = 14;

    /// <summary><c>DWM_CLOAKED_APP</c>: cloaked by its own app (the only reason the app can undo).</summary>
    public const int DwmCloakedApp = 0x1;

    /// <summary><c>WM_STYLECHANGING</c>: the style is about to change; <c>lParam</c> points at a <see cref="StyleStruct"/> whose new value may be edited.</summary>
    public const uint WmStyleChanging = 0x007C;

    /// <summary><c>WM_MOUSEACTIVATE</c>: a click on an inactive window; the answer says whether it activates.</summary>
    public const uint WmMouseActivate = 0x0021;

    /// <summary><c>WM_NCDESTROY</c>: the last message a window gets.</summary>
    public const uint WmNcDestroy = 0x0082;

    /// <summary><c>MA_NOACTIVATE</c>: the click goes to the window, which does not activate.</summary>
    public const nint MaNoActivate = 3;

    /// <summary><c>RGN_OR</c>: the union of two regions.</summary>
    public const int RgnOr = 2;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;

    /// <summary><c>STYLESTRUCT</c>: the old and the new style of a <see cref="WmStyleChanging"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StyleStruct
    {
        public uint StyleOld;
        public uint StyleNew;
    }

    /// <summary><c>LONG_PTR GetWindowLongPtrW(HWND hWnd, int nIndex)</c>.</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtrW(nint window, int index);

    /// <summary><c>LONG_PTR SetWindowLongPtrW(HWND hWnd, int nIndex, LONG_PTR dwNewLong)</c>.</summary>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    /// <summary><c>LRESULT CallWindowProcW(WNDPROC lpPrevWndFunc, HWND hWnd, UINT Msg, WPARAM wParam, LPARAM lParam)</c>.</summary>
    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    public static partial nint CallWindowProcW(nint previous, nint window, uint message, nint wParam, nint lParam);

    /// <summary><c>LRESULT DefWindowProcW(HWND, UINT, WPARAM, LPARAM)</c>.</summary>
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    /// <summary><c>BOOL IsWindow(HWND hWnd)</c>.</summary>
    [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint window);

    /// <summary><c>BOOL IsWindowEnabled(HWND hWnd)</c>.</summary>
    [LibraryImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowEnabled(nint window);

    /// <summary><c>BOOL EnableWindow(HWND hWnd, BOOL bEnable)</c>: returns whether the window was disabled before.</summary>
    [LibraryImport("user32.dll", EntryPoint = "EnableWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enable);

    /// <summary><c>HRESULT DwmGetWindowAttribute(HWND, DWORD, PVOID, DWORD)</c> for a 32-bit attribute.</summary>
    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static partial int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    /// <summary><c>HRESULT DwmSetWindowAttribute(HWND, DWORD, LPCVOID, DWORD)</c> for a 32-bit attribute.</summary>
    [LibraryImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    public static partial int DwmSetWindowAttribute(nint window, int attribute, in int value, int size);

    /// <summary><c>BOOL SetWindowPos(HWND hWnd, HWND hWndInsertAfter, int X, int Y, int cx, int cy, UINT uFlags)</c>.</summary>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    /// <summary><c>int SetWindowRgn(HWND hWnd, HRGN hRgn, BOOL bRedraw)</c>: 0 on failure. On success the system owns the region.</summary>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowRgn", SetLastError = true)]
    public static partial int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    /// <summary><c>HRGN CreateRectRgn(int x1, int y1, int x2, int y2)</c>: 0 on failure.</summary>
    [LibraryImport("gdi32.dll", EntryPoint = "CreateRectRgn")]
    public static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    /// <summary><c>int CombineRgn(HRGN hrgnDst, HRGN hrgnSrc1, HRGN hrgnSrc2, int iMode)</c>: 0 (ERROR) on failure.</summary>
    [LibraryImport("gdi32.dll", EntryPoint = "CombineRgn")]
    public static partial int CombineRgn(nint destination, nint first, nint second, int mode);

    /// <summary><c>BOOL DeleteObject(HGDIOBJ ho)</c>.</summary>
    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint handle);
}
