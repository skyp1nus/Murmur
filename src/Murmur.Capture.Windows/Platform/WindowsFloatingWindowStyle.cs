using Murmur.Core.Platform;

namespace Murmur.Capture.Windows.Platform;

/// <summary>
/// <see cref="IFloatingWindowStyle"/> on Windows: adds <c>WS_EX_NOACTIVATE</c> (a click never activates the window, so focus stays with
/// the call) and <c>WS_EX_TOOLWINDOW</c> (no Alt+Tab entry) to the extended style, drops <c>WS_EX_APPWINDOW</c>, and has the frame
/// re-read with <c>SetWindowPos(SWP_FRAMECHANGED)</c> without moving, sizing, reordering or activating anything. Failures are
/// swallowed: the orb still works, it just may take focus on a click.
/// </summary>
internal sealed class WindowsFloatingWindowStyle : IFloatingWindowStyle
{
    /// <summary>The extended style with the non-activating, tool-window bits set and the app-window bit cleared (the flag maths, testable on any OS).</summary>
    internal static nint NonActivatingStyle(nint current) =>
        (current | FloatingWindowNativeMethods.WsExNoActivate | FloatingWindowNativeMethods.WsExToolWindow) & ~FloatingWindowNativeMethods.WsExAppWindow;

    /// <summary>The flags of the <c>SetWindowPos</c> call that only makes the window re-read its frame.</summary>
    internal const uint FrameChangeOnly =
        FloatingWindowNativeMethods.SwpNoMove
        | FloatingWindowNativeMethods.SwpNoSize
        | FloatingWindowNativeMethods.SwpNoZOrder
        | FloatingWindowNativeMethods.SwpNoActivate
        | FloatingWindowNativeMethods.SwpFrameChanged;

    public void MakeNonActivating(nint nativeHandle)
    {
        if (nativeHandle == 0)
        {
            return;
        }

        try
        {
            var current = FloatingWindowNativeMethods.GetWindowLongPtrW(nativeHandle, FloatingWindowNativeMethods.GwlExStyle);
            var wanted = NonActivatingStyle(current);
            if (wanted == current)
            {
                return;
            }

            FloatingWindowNativeMethods.SetWindowLongPtrW(nativeHandle, FloatingWindowNativeMethods.GwlExStyle, wanted);
            FloatingWindowNativeMethods.SetWindowPos(nativeHandle, 0, 0, 0, 0, 0, FrameChangeOnly);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"Making the floating window non-activating failed: {ex.Message}");
        }
    }
}
