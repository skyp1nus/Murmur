using System.Runtime.InteropServices;

namespace Murmur.Capture.Windows.Input.Interop;

// Blittable mirrors of the Win32 structs used by the global-hotkey window (Windows SDK 10.0.26100 winuser.h); sizes and
// offsets are asserted by HotkeyInteropLayoutTests.

/// <summary>winuser.h <c>WNDCLASSEXW</c> (80 bytes on x64). Pointers are <c>nint</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WndClassExW
{
    public uint Size;
    public uint Style;

    /// <summary><c>WNDPROC</c>: an <c>[UnmanagedCallersOnly]</c> function pointer.</summary>
    public nint WindowProc;
    public int ClassExtra;
    public int WindowExtra;
    public nint Instance;
    public nint Icon;
    public nint Cursor;
    public nint Background;
    public nint MenuName;

    /// <summary><c>LPCWSTR</c>; must stay valid during <c>RegisterClassExW</c> only.</summary>
    public nint ClassName;
    public nint IconSmall;
}

/// <summary>winuser.h <c>MSG</c> (48 bytes on x64).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    public nint Window;
    public uint Message;
    public nint WParam;
    public nint LParam;
    public uint Time;
    public int PointX;
    public int PointY;
}
