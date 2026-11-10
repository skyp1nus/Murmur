using System.Runtime.InteropServices;
using Murmur.Core.Platform;

namespace Murmur.Capture.Windows.Platform;

/// <summary>
/// <see cref="IFloatingWindowStyle"/> on Windows: makes the floating orb's window non-activating and keeps it so, whatever Avalonia does
/// to the window's style later.
/// </summary>
/// <remarks>
/// <para>Setting <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c> once is not enough: Avalonia rebuilds the extended style from its own
/// properties on <c>Show</c>, when <c>Topmost</c> changes and on other property changes, and writes it with <c>SetWindowLong</c>, which
/// drops both bits (seen on the owner's PC: <c>GWL_EXSTYLE = 0x200008</c> after the first show). So the window is subclassed
/// (<c>GWLP_WNDPROC</c>, an <c>[UnmanagedCallersOnly]</c> procedure that calls the previous one for everything else):</para>
/// <list type="bullet">
/// <item><c>WM_STYLECHANGING</c> for <c>GWL_EXSTYLE</c>: the bits are put back into the new style before it is stored, so every later
/// write, by anyone, keeps them;</item>
/// <item><c>WM_MOUSEACTIVATE</c>: answers <c>MA_NOACTIVATE</c>, so a click never activates the window even if the style were lost;</item>
/// <item><c>WM_NCDESTROY</c>: the previous procedure is put back and the window forgotten.</item>
/// </list>
/// <para>Each call also makes sure the window can be seen and clicked (<see cref="MakeReachable"/>).</para>
/// <para><see cref="SetInputRegion"/> sets a window region: outside it, clicks go to the window below (see the method).</para>
/// <para>All calls come from the UI thread that owns the window (a window can only be subclassed from its own process). Failures are
/// reported on stderr and swallowed: the orb still works, it may just take focus on a click.</para>
/// </remarks>
internal sealed unsafe class WindowsFloatingWindowStyle : IFloatingWindowStyle
{
    /// <summary>The subclassed windows and the procedure each had before.</summary>
    private static readonly Dictionary<nint, nint> s_previous = [];
    private static readonly Lock s_lock = new();

    /// <summary>
    /// The extended style with the non-activating, tool-window bits set, and the app-window, transparent and layered bits cleared (the flag
    /// maths, testable on any OS). The last two would make the orb let clicks through, and a layered window is not drawn until it gets
    /// layer content: the symptom seen once on the owner's PC (an orb that was "visible" to Win32 but neither drawn nor clickable).
    /// </summary>
    internal static nint NonActivatingStyle(nint current) =>
        (current | FloatingWindowNativeMethods.WsExNoActivate | FloatingWindowNativeMethods.WsExToolWindow)
        & ~(FloatingWindowNativeMethods.WsExAppWindow | FloatingWindowNativeMethods.WsExTransparent | FloatingWindowNativeMethods.WsExLayered);

    /// <summary>The same maths on the 32-bit value of a <c>STYLESTRUCT</c>.</summary>
    internal static uint NonActivatingStyle(uint current) => (uint)NonActivatingStyle((nint)current);

    /// <summary>The flags of the <c>SetWindowPos</c> call that only makes the window re-read its frame.</summary>
    internal const uint FrameChangeOnly =
        FloatingWindowNativeMethods.SwpNoMove
        | FloatingWindowNativeMethods.SwpNoSize
        | FloatingWindowNativeMethods.SwpNoZOrder
        | FloatingWindowNativeMethods.SwpNoActivate
        | FloatingWindowNativeMethods.SwpFrameChanged;

    /// <summary>The window is subclassed (for tests).</summary>
    internal static bool IsSubclassed(nint window)
    {
        lock (s_lock)
        {
            return s_previous.ContainsKey(window);
        }
    }

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
            Subclass(nativeHandle);
            ApplyStyle(nativeHandle);
            MakeReachable(nativeHandle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"Making the floating window non-activating failed: {ex.Message}");
        }
    }

    public void SetInputRegion(nint nativeHandle, ReadOnlySpan<FloatingWindowRect> rectangles)
    {
        if (nativeHandle == 0)
        {
            return;
        }

        // A window region, not WM_NCHITTEST -> HTTRANSPARENT: HTTRANSPARENT only passes a click on to windows of the same thread, while
        // the window manager routes a click outside the region to whatever window is below, in any process. The region also clips
        // drawing, so the rectangles include the shadow margin; bRedraw is FALSE because nothing outside them is ever drawn.
        try
        {
            var region = CreateRegion(rectangles);
            if (rectangles.Length > 0 && region == 0)
            {
                Console.Error.WriteLine("Creating the floating window's input region failed.");
                return;
            }

            if (FloatingWindowNativeMethods.SetWindowRgn(nativeHandle, region, redraw: false) == 0)
            {
                // The region is still ours only when the call failed.
                Console.Error.WriteLine($"Setting the floating window's input region failed (Win32 error {Marshal.GetLastPInvokeError()}).");
                if (region != 0)
                {
                    FloatingWindowNativeMethods.DeleteObject(region);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"Setting the floating window's input region failed: {ex.Message}");
        }
    }

    /// <summary>The union of the rectangles as one GDI region (0 for none, or when GDI fails); empty rectangles are skipped.</summary>
    private static nint CreateRegion(ReadOnlySpan<FloatingWindowRect> rectangles)
    {
        nint region = 0;
        foreach (var rectangle in rectangles)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                continue;
            }

            var part = FloatingWindowNativeMethods.CreateRectRgn(rectangle.X, rectangle.Y, rectangle.Right, rectangle.Bottom);
            if (part == 0)
            {
                continue;
            }

            if (region == 0)
            {
                region = part;
                continue;
            }

            FloatingWindowNativeMethods.CombineRgn(region, region, part, FloatingWindowNativeMethods.RgnOr);
            FloatingWindowNativeMethods.DeleteObject(part);
        }

        return region;
    }

    /// <summary>Puts the window procedure that keeps the style and refuses mouse activation in front of the window's own, once.</summary>
    private static void Subclass(nint window)
    {
        lock (s_lock)
        {
            if (s_previous.ContainsKey(window))
            {
                return;
            }

            Marshal.SetLastPInvokeError(0);
            delegate* unmanaged<nint, uint, nint, nint, nint> procedure = &WindowProc;
            var previous = FloatingWindowNativeMethods.SetWindowLongPtrW(window, FloatingWindowNativeMethods.GwlpWndProc, (nint)procedure);
            if (previous == 0)
            {
                // Not a window, or one of another process: nothing was replaced.
                Console.Error.WriteLine($"Subclassing the floating window failed (Win32 error {Marshal.GetLastPInvokeError()}).");
                return;
            }

            s_previous[window] = previous;
        }
    }

    /// <summary>Sets the bits now (the window may already be shown) and has the frame re-read without moving, sizing, reordering or activating it.</summary>
    private static void ApplyStyle(nint window)
    {
        Marshal.SetLastPInvokeError(0);
        var current = FloatingWindowNativeMethods.GetWindowLongPtrW(window, FloatingWindowNativeMethods.GwlExStyle);
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
        var previous = FloatingWindowNativeMethods.SetWindowLongPtrW(window, FloatingWindowNativeMethods.GwlExStyle, wanted);
        if (previous == 0 && Marshal.GetLastPInvokeError() is var writeError and not 0)
        {
            Console.Error.WriteLine($"Making the floating window non-activating failed (Win32 error {writeError}).");
            return;
        }

        FloatingWindowNativeMethods.SetWindowPos(window, 0, 0, 0, 0, 0, FrameChangeOnly);
    }

    /// <summary>
    /// The window must be drawn and take clicks: it is enabled again if something disabled it (a disabled window is skipped by
    /// <c>WindowFromPoint</c>), and uncloaked if this app cloaked it. A window cloaked by the shell (it is on another virtual desktop) is
    /// only reported: that is the user's arrangement, not ours to undo.
    /// </summary>
    private static void MakeReachable(nint window)
    {
        if (!FloatingWindowNativeMethods.IsWindowEnabled(window))
        {
            FloatingWindowNativeMethods.EnableWindow(window, enable: true);
            Console.Error.WriteLine("The floating window was disabled; it was enabled again.");
        }

        try
        {
            if (FloatingWindowNativeMethods.DwmGetWindowAttribute(window, FloatingWindowNativeMethods.DwmwaCloaked, out var cloaked, sizeof(int)) != 0
                || cloaked == 0)
            {
                return;
            }

            if ((cloaked & FloatingWindowNativeMethods.DwmCloakedApp) != 0)
            {
                FloatingWindowNativeMethods.DwmSetWindowAttribute(window, FloatingWindowNativeMethods.DwmwaCloak, 0, sizeof(int));
                Console.Error.WriteLine("The floating window was cloaked by the app; it was uncloaked.");
            }
            else
            {
                Console.Error.WriteLine($"The floating window is cloaked by the system (DWMWA_CLOAKED {cloaked}: another virtual desktop?).");
            }
        }
        catch (DllNotFoundException)
        {
            // No DWM: nothing is ever cloaked.
        }
    }

    /// <summary>
    /// What the subclass answers itself (pure, for tests): <c>MA_NOACTIVATE</c> to <c>WM_MOUSEACTIVATE</c>; <c>null</c> for every other
    /// message, which goes on to the window's own procedure.
    /// </summary>
    internal static nint? OwnAnswer(uint message) =>
        message == FloatingWindowNativeMethods.WmMouseActivate ? FloatingWindowNativeMethods.MaNoActivate : null;

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        nint previous;
        lock (s_lock)
        {
            if (!s_previous.TryGetValue(window, out previous))
            {
                return FloatingWindowNativeMethods.DefWindowProcW(window, message, wParam, lParam);
            }

            if (message == FloatingWindowNativeMethods.WmNcDestroy)
            {
                s_previous.Remove(window);
                FloatingWindowNativeMethods.SetWindowLongPtrW(window, FloatingWindowNativeMethods.GwlpWndProc, previous);
            }
        }

        if (OwnAnswer(message) is { } answer)
        {
            return answer;
        }

        if (message == FloatingWindowNativeMethods.WmStyleChanging && wParam == FloatingWindowNativeMethods.GwlExStyle && lParam != 0)
        {
            var change = (FloatingWindowNativeMethods.StyleStruct*)lParam;
            change->StyleNew = NonActivatingStyle(change->StyleNew);
        }

        return FloatingWindowNativeMethods.CallWindowProcW(previous, window, message, wParam, lParam);
    }
}
