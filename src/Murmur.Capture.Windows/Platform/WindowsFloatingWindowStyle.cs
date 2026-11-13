using System.Globalization;
using System.Runtime.InteropServices;
using Murmur.Core.Diagnostics;
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
/// <para><c>MA_NOACTIVATE</c> only stops the click itself from activating the window: Avalonia's <c>WindowImpl</c> then calls
/// <c>SetFocus(hwnd)</c> on every <c>WM_*BUTTONDOWN</c> (<c>ShouldTakeFocusOnClick</c> is true for windows), and <c>SetFocus</c>
/// activates the window it focuses. So while an orb window exists, a thread-local <c>WH_CBT</c> hook on the UI thread refuses
/// <c>HCBT_ACTIVATE</c> and <c>HCBT_SETFOCUS</c> for the subclassed windows (and for no other window); <c>SetFocus</c> then fails
/// and nothing is activated. The button-down message itself still reaches Avalonia, so clicks and drags work as before: swallowing it
/// to skip the <c>SetFocus</c> would also take the press away from Avalonia's input. The hook is removed with the last orb window.</para>
/// <para>Each call also makes sure the window can be seen and clicked (<see cref="MakeReachable"/>).</para>
/// <para><see cref="SetInputRegion"/> sets a window region: outside it, clicks go to the window below (see the method).</para>
/// <para>All calls come from the UI thread that owns the window (a window can only be subclassed from its own process). Failures are
/// written to the <see cref="IAppLog"/> (area <c>orb</c>, each code once per instance, so a failure that repeats on every call does not
/// flood the log) and swallowed: the orb still works, it may just take focus on a click. The window procedure and the hook never log.</para>
/// </remarks>
internal sealed unsafe class WindowsFloatingWindowStyle : IFloatingWindowStyle
{
    private const string Area = "orb";

    /// <summary>The subclassed windows and the procedure each had before.</summary>
    private static readonly Dictionary<nint, nint> s_previous = [];
    private static readonly Lock s_lock = new();

    /// <summary>The thread-local <c>WH_CBT</c> hook of the UI thread while at least one window is subclassed (0 otherwise).</summary>
    private static nint s_hook;

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

    /// <summary>The activation hook is installed (for tests).</summary>
    internal static bool IsHooked
    {
        get
        {
            lock (s_lock)
            {
                return s_hook != 0;
            }
        }
    }

    /// <summary>The window is subclassed (for tests).</summary>
    internal static bool IsSubclassed(nint window)
    {
        lock (s_lock)
        {
            return s_previous.ContainsKey(window);
        }
    }

    private readonly IAppLog _log;

    /// <summary>The failures already written (a bit per <see cref="Failure"/>), so one that repeats is logged once.</summary>
    private int _reported;

    /// <param name="log">The diagnostic log (#100) the failures go to; <see cref="NullAppLog"/> by default.</param>
    public WindowsFloatingWindowStyle(IAppLog? log = null) => _log = log ?? NullAppLog.Instance;

    /// <summary>The failures the style reports; each is written at most once per instance.</summary>
    private enum Failure
    {
        StyleUnsupported,
        RegionCreate,
        RegionSet,
        RegionUnsupported,
        SubclassFailed,
        HookInstall,
        StyleRead,
        StyleWrite,
        WindowDisabled,
        WindowUncloaked,
        CloakedBySystem,
    }

    /// <summary>The detail of a failed Win32 call: <c>Win32 error 1400</c> (the code only, never a message).</summary>
    internal static string Win32Detail(int error) => string.Create(CultureInfo.InvariantCulture, $"Win32 error {error}");

    /// <summary>The detail of a window cloaked by the system: <c>DWMWA_CLOAKED 0x00000004</c>.</summary>
    internal static string CloakedDetail(int cloaked) => string.Create(CultureInfo.InvariantCulture, $"DWMWA_CLOAKED 0x{cloaked:X8}");

    /// <summary>Writes the event unless this instance already wrote this failure. The detail is built only on the failure path.</summary>
    private void Report(Failure failure, LogLevel level, string code, string? detail)
    {
        var bit = 1 << (int)failure;
        if ((Interlocked.Or(ref _reported, bit) & bit) == 0)
        {
            _log.Write(level, Area, code, detail);
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
            Report(Failure.StyleUnsupported, LogLevel.Error, "style.unsupported", LogText.Error(ex));
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
                Report(Failure.RegionCreate, LogLevel.Warning, "region.create-failed", null);
                return;
            }

            if (FloatingWindowNativeMethods.SetWindowRgn(nativeHandle, region, redraw: false) == 0)
            {
                // The region is still ours only when the call failed.
                Report(Failure.RegionSet, LogLevel.Warning, "region.set-failed", Win32Detail(Marshal.GetLastPInvokeError()));
                if (region != 0)
                {
                    FloatingWindowNativeMethods.DeleteObject(region);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Report(Failure.RegionUnsupported, LogLevel.Error, "region.unsupported", LogText.Error(ex));
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
    private void Subclass(nint window)
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
                Report(Failure.SubclassFailed, LogLevel.Error, "style.subclass-failed", Win32Detail(Marshal.GetLastPInvokeError()));
                return;
            }

            s_previous[window] = previous;
            if (s_hook == 0)
            {
                delegate* unmanaged<int, nint, nint, nint> hook = &CbtProc;
                s_hook = FloatingWindowNativeMethods.SetWindowsHookExW(
                    FloatingWindowNativeMethods.WhCbt, (nint)hook, 0, FloatingWindowNativeMethods.GetCurrentThreadId());
                if (s_hook == 0)
                {
                    Report(Failure.HookInstall, LogLevel.Warning, "hook.install-failed", Win32Detail(Marshal.GetLastPInvokeError()));
                }
            }
        }
    }

    /// <summary>
    /// What the CBT hook decides (pure, for tests): refuse (<c>true</c>) the activation of, or the focus to, a subclassed orb window;
    /// everything else goes on to the next hook.
    /// </summary>
    internal static bool Refuses(int code, bool isOrb) =>
        isOrb && (code == FloatingWindowNativeMethods.HcbtActivate || code == FloatingWindowNativeMethods.HcbtSetFocus);

    [UnmanagedCallersOnly]
    private static nint CbtProc(int code, nint wParam, nint lParam)
    {
        nint hook;
        bool isOrb;
        lock (s_lock)
        {
            hook = s_hook;
            isOrb = code >= 0 && s_previous.ContainsKey(wParam);
        }

        return Refuses(code, isOrb) ? 1 : FloatingWindowNativeMethods.CallNextHookEx(hook, code, wParam, lParam);
    }

    /// <summary>Sets the bits now (the window may already be shown) and has the frame re-read without moving, sizing, reordering or activating it.</summary>
    private void ApplyStyle(nint window)
    {
        Marshal.SetLastPInvokeError(0);
        var current = FloatingWindowNativeMethods.GetWindowLongPtrW(window, FloatingWindowNativeMethods.GwlExStyle);
        if (current == 0 && Marshal.GetLastPInvokeError() is var readError and not 0)
        {
            // A style that could not be read is not "no style": writing only the new bits would strip every other one.
            Report(Failure.StyleRead, LogLevel.Warning, "style.read-failed", Win32Detail(readError));
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
            Report(Failure.StyleWrite, LogLevel.Error, "style.write-failed", Win32Detail(writeError));
            return;
        }

        FloatingWindowNativeMethods.SetWindowPos(window, 0, 0, 0, 0, 0, FrameChangeOnly);
    }

    /// <summary>
    /// The window must be drawn and take clicks: it is enabled again if something disabled it (a disabled window is skipped by
    /// <c>WindowFromPoint</c>), and uncloaked if this app cloaked it. A window cloaked by the shell (it is on another virtual desktop) is
    /// only reported: that is the user's arrangement, not ours to undo.
    /// </summary>
    private void MakeReachable(nint window)
    {
        if (!FloatingWindowNativeMethods.IsWindowEnabled(window))
        {
            FloatingWindowNativeMethods.EnableWindow(window, enable: true);
            Report(Failure.WindowDisabled, LogLevel.Warning, "window.reenabled", null);
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
                Report(Failure.WindowUncloaked, LogLevel.Warning, "window.uncloaked", null);
            }
            else
            {
                Report(Failure.CloakedBySystem, LogLevel.Warning, "window.cloaked-by-system", CloakedDetail(cloaked));
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
                if (s_previous.Count == 0 && s_hook != 0)
                {
                    FloatingWindowNativeMethods.UnhookWindowsHookEx(s_hook);
                    s_hook = 0;
                }
            }
        }

        if (OwnAnswer(message) is { } answer)
        {
            return answer;
        }

        if (message == FloatingWindowNativeMethods.WmStyleChanging && (int)wParam == FloatingWindowNativeMethods.GwlExStyle && lParam != 0)
        {
            var change = (FloatingWindowNativeMethods.StyleStruct*)lParam;
            change->StyleNew = NonActivatingStyle(change->StyleNew);
        }

        return FloatingWindowNativeMethods.CallWindowProcW(previous, window, message, wParam, lParam);
    }
}
