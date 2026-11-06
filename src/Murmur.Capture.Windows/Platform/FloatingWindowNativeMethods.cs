using System.Runtime.InteropServices;

namespace Murmur.Capture.Windows.Platform;

/// <summary>Win32 imports for the floating recorder orb's window style (user32), source-generated; values follow the Windows SDK 10.0.26100 headers.</summary>
internal static partial class FloatingWindowNativeMethods
{
    /// <summary><c>GWL_EXSTYLE</c>: the index of the extended window style.</summary>
    public const int GwlExStyle = -20;

    /// <summary><c>WS_EX_NOACTIVATE</c>: a click on the window does not activate it.</summary>
    public const nint WsExNoActivate = 0x08000000;

    /// <summary><c>WS_EX_TOOLWINDOW</c>: a tool window; it is not in Alt+Tab or the taskbar.</summary>
    public const nint WsExToolWindow = 0x00000080;

    /// <summary><c>WS_EX_APPWINDOW</c>: forces a taskbar button and an Alt+Tab entry; cleared so the tool-window style wins.</summary>
    public const nint WsExAppWindow = 0x00040000;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;

    /// <summary><c>LONG_PTR GetWindowLongPtrW(HWND hWnd, int nIndex)</c>.</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtrW(nint window, int index);

    /// <summary><c>LONG_PTR SetWindowLongPtrW(HWND hWnd, int nIndex, LONG_PTR dwNewLong)</c>.</summary>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    /// <summary><c>BOOL SetWindowPos(HWND hWnd, HWND hWndInsertAfter, int X, int Y, int cx, int cy, UINT uFlags)</c>.</summary>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
