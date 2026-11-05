using System.Runtime.InteropServices;
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

        // x86 is not supported: the *LongPtrW entry points do not exist in the 32-bit user32 (EntryPointNotFoundException, caught below);
        // the app ships for x64 only.
        try
        {
            Marshal.SetLastPInvokeError(0);
            var current = FloatingWindowNativeMethods.GetWindowLongPtrW(nativeHandle, FloatingWindowNativeMethods.GwlExStyle);
            if (current == 0 && Marshal.GetLastPInvokeError() is var readError and not 0)
            {
                // A style that could not be read is not "no style": writing only the new bits would strip every other one.
                Console.Error.WriteLine($"Reading the floating window's style failed (Win32 error {readError}).");
                return;
            }

            var wanted = NonActivatingStyle(current);
            if (wanted == current)
            {
                return;
            }

            Marshal.SetLastPInvokeError(0);
            var previous = FloatingWindowNativeMethods.SetWindowLongPtrW(nativeHandle, FloatingWindowNativeMethods.GwlExStyle, wanted);
            if (previous == 0 && Marshal.GetLastPInvokeError() is var writeError and not 0)
            {
                Console.Error.WriteLine($"Making the floating window non-activating failed (Win32 error {writeError}).");
                return;
            }

            FloatingWindowNativeMethods.SetWindowPos(nativeHandle, 0, 0, 0, 0, 0, FrameChangeOnly);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"Making the floating window non-activating failed: {ex.Message}");
        }
    }
}
