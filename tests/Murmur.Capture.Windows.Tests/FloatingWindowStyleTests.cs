using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Platform;

namespace Murmur.Capture.Windows.Tests;

/// <summary>The extended-style maths of the floating orb's window (#46), on any OS, and the Win32 behaviour itself on Windows.</summary>
public sealed partial class FloatingWindowStyleTests
{
    private const nint NoActivate = 0x08000000;
    private const nint ToolWindow = 0x00000080;
    private const nint AppWindow = 0x00040000;
    private const nint TopMost = 0x00000008;

    /// <summary>
    /// <c>WS_EX_NOREDIRECTIONBITMAP</c>: Avalonia's window has it from its creation. Windows (seen on Windows 11 build 26300) ignores a whole
    /// <c>SetWindowLong(GWL_EXSTYLE)</c> whose value adds this bit to a window created without it, so the test windows that stand in for
    /// Avalonia's are created with it; otherwise the "rewrite" changes nothing and the tests around it prove nothing.
    /// </summary>
    private const nint NoRedirectionBitmap = 0x00200000;

    private const string WindowsOnly = "user32 runs only on Windows.";

    /// <summary>What Avalonia writes for the orb's window (<c>WS_EX_TOPMOST | WS_EX_NOREDIRECTIONBITMAP</c>, read on the owner's PC).</summary>
    private const nint AvaloniaStyle = 0x200008;

    [Fact]
    public void TheValues_AreTheOnesOfWinuserH()
    {
        Assert.Equal(-20, FloatingWindowNativeMethods.GwlExStyle);
        Assert.Equal(-4, FloatingWindowNativeMethods.GwlpWndProc);
        Assert.Equal(NoActivate, FloatingWindowNativeMethods.WsExNoActivate);
        Assert.Equal(ToolWindow, FloatingWindowNativeMethods.WsExToolWindow);
        Assert.Equal(AppWindow, FloatingWindowNativeMethods.WsExAppWindow);
        Assert.Equal(0x7Cu, FloatingWindowNativeMethods.WmStyleChanging);
        Assert.Equal(0x21u, FloatingWindowNativeMethods.WmMouseActivate);
        Assert.Equal(0x82u, FloatingWindowNativeMethods.WmNcDestroy);
        Assert.Equal(3, FloatingWindowNativeMethods.MaNoActivate);
        Assert.Equal(0x0037u, WindowsFloatingWindowStyle.FrameChangeOnly);
        Assert.Equal(8, Marshal.SizeOf<FloatingWindowNativeMethods.StyleStruct>());
        Assert.Equal(0x20, FloatingWindowNativeMethods.WsExTransparent);
        Assert.Equal(0x80000, FloatingWindowNativeMethods.WsExLayered);
        Assert.Equal(13, FloatingWindowNativeMethods.DwmwaCloak);
        Assert.Equal(14, FloatingWindowNativeMethods.DwmwaCloaked);
    }

    [Fact]
    public void ThePlainStyle_GainsNoActivateAndToolWindow()
    {
        var style = WindowsFloatingWindowStyle.NonActivatingStyle(0);

        Assert.Equal(NoActivate | ToolWindow, style);
    }

    [Fact]
    public void OtherBits_Stay_AndTheAppWindowBitGoes()
    {
        var style = WindowsFloatingWindowStyle.NonActivatingStyle(TopMost | AppWindow);

        Assert.Equal(TopMost | NoActivate | ToolWindow, style);
    }

    [Fact]
    public void TheClickThroughBits_TransparentAndLayered_AreCleared()
    {
        const nint transparent = 0x20;
        const nint layered = 0x80000;

        var style = WindowsFloatingWindowStyle.NonActivatingStyle(TopMost | transparent | layered);

        Assert.Equal(TopMost | NoActivate | ToolWindow, style);
    }

    [Fact]
    public void TheStyleStructMaths_IsTheSame()
    {
        Assert.Equal((uint)(AvaloniaStyle | NoActivate | ToolWindow), WindowsFloatingWindowStyle.NonActivatingStyle((uint)AvaloniaStyle));
    }

    [Fact]
    public void TheMaths_IsIdempotent()
    {
        var once = WindowsFloatingWindowStyle.NonActivatingStyle(TopMost);

        Assert.Equal(once, WindowsFloatingWindowStyle.NonActivatingStyle(once));
    }

    [Fact]
    public void TheFrameCallMovesSizesReordersAndActivatesNothing()
    {
        const uint noMove = 0x2;
        const uint noSize = 0x1;
        const uint noZOrder = 0x4;
        const uint noActivate = 0x10;
        const uint frameChanged = 0x20;

        Assert.Equal(noMove | noSize | noZOrder | noActivate | frameChanged, WindowsFloatingWindowStyle.FrameChangeOnly);
    }

    [Fact]
    public void TheSubclass_AnswersMouseActivationWithNoActivate_AndPassesEverythingElseOn()
    {
        Assert.Equal((nint)3, WindowsFloatingWindowStyle.OwnAnswer(0x21));
        Assert.Null(WindowsFloatingWindowStyle.OwnAnswer(0x7C));
        Assert.Null(WindowsFloatingWindowStyle.OwnAnswer(0x84));
        Assert.Null(WindowsFloatingWindowStyle.OwnAnswer(0x201));
    }

    [Fact]
    public void ANullHandle_IsIgnored()
    {
        new WindowsFloatingWindowStyle().MakeNonActivating(0);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void AHandleThatIsNotAWindow_DoesNotThrow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        new WindowsFloatingWindowStyle().MakeNonActivating(0x7FFFFFF);

        Assert.False(WindowsFloatingWindowStyle.IsSubclassed(0x7FFFFFF));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void AfterShow_TheWindowIsNonActivatingAndAToolWindow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var window = TestWindow.Create();
        var style = new WindowsFloatingWindowStyle();

        style.MakeNonActivating(window.Handle);
        window.ShowNoActivate();
        style.MakeNonActivating(window.Handle);

        AssertNonActivating(window.ExtendedStyle);
        Assert.True(WindowsFloatingWindowStyle.IsSubclassed(window.Handle));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void AStyleRewrittenAfterTheShow_KeepsTheBits_LikeAvaloniaOnShowAndTopmost()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var window = TestWindow.Create(exStyle: (uint)NoRedirectionBitmap);
        new WindowsFloatingWindowStyle().MakeNonActivating(window.Handle);
        window.ShowNoActivate();

        // What Avalonia's UpdateWindowProperties does on Show and on a Topmost change: SetWindowLong(GWL_EXSTYLE) with its own value.
        window.SetExtendedStyle(AvaloniaStyle);
        AssertNonActivating(window.ExtendedStyle);

        window.SetTopmost(false);
        window.SetExtendedStyle(AvaloniaStyle & ~TopMost);
        window.SetTopmost(true);
        window.SetExtendedStyle(AvaloniaStyle);

        AssertNonActivating(window.ExtendedStyle);
        Assert.Equal(TopMost, window.ExtendedStyle & TopMost);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void WithoutTheSubclass_ARewriteDropsTheBits_SoTheTestAboveMeansSomething()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var window = TestWindow.Create(exStyle: (uint)NoRedirectionBitmap);
        window.SetExtendedStyle(NoActivate | ToolWindow | NoRedirectionBitmap);
        window.ShowNoActivate();

        window.SetExtendedStyle(AvaloniaStyle);

        Assert.Equal((nint)0, window.ExtendedStyle & NoActivate);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void WmMouseActivate_IsAnsweredWithMaNoActivate()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var window = TestWindow.Create();
        new WindowsFloatingWindowStyle().MakeNonActivating(window.Handle);
        window.ShowNoActivate();

        // wParam = the top-level window, lParam = MAKELPARAM(HTCLIENT, WM_LBUTTONDOWN).
        var answer = window.Send(0x21, window.Handle, (0x201 << 16) | 1);

        Assert.Equal((nint)3, answer);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void Subclassing_IsDoneOnce_AndUndoneWhenTheWindowIsDestroyed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        nint handle;
        using (var window = TestWindow.Create())
        {
            handle = window.Handle;
            var style = new WindowsFloatingWindowStyle();
            style.MakeNonActivating(handle);
            var procedure = window.Procedure;
            style.MakeNonActivating(handle);

            Assert.Equal(procedure, window.Procedure);
            Assert.True(WindowsFloatingWindowStyle.IsSubclassed(handle));
        }

        Assert.False(WindowsFloatingWindowStyle.IsSubclassed(handle));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void TheInputRegion_IsTheUnionOfTheRectangles_AndClicksOutsideItGoToTheWindowBelow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var window = TestWindow.Create(x: 100, y: 100, width: 200, height: 124);
        var style = new WindowsFloatingWindowStyle();
        style.MakeNonActivating(window.Handle);
        window.ShowNoActivate();
        window.SetTopmost(true);

        style.SetInputRegion(window.Handle, [new(148, 36, 52, 52), new(0, 38, 150, 48)]);

        // WindowFromPoint answers with whatever is topmost at the point, so a foreign topmost window over (100, 100) would fail the test:
        // the message names the window found instead of two bare handles.
        Assert.Equal((0, 36, 200, 88), window.RegionBox);
        AssertWindowAt(window, 100 + 170, 100 + 60, expectOrb: true);
        AssertWindowAt(window, 100 + 20, 100 + 60, expectOrb: true);
        AssertWindowAt(window, 100 + 20, 100 + 10, expectOrb: false);
        AssertWindowAt(window, 100 + 170, 100 + 110, expectOrb: false);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void AnEmptyInputRegion_GivesTheWholeWindowItsInputBack()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var window = TestWindow.Create(x: 100, y: 100, width: 200, height: 124);
        var style = new WindowsFloatingWindowStyle();
        window.ShowNoActivate();
        style.SetInputRegion(window.Handle, [new(148, 36, 52, 52)]);

        style.SetInputRegion(window.Handle, []);

        Assert.Null(window.RegionBox);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void ADisabledCloakedOrClickThroughWindow_IsMadeReachableAgain()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var window = TestWindow.Create();
        window.ShowNoActivate();
        window.SetExtendedStyle(0x20 | 0x80000);
        window.Enable(false);
        window.Cloak(true);

        new WindowsFloatingWindowStyle().MakeNonActivating(window.Handle);

        Assert.Equal((nint)0, window.ExtendedStyle & (0x20 | 0x80000));
        Assert.True(window.IsEnabled);
        Assert.Equal(0, window.Cloaked);
    }

    [Theory]
    [InlineData(5, true, true)]
    [InlineData(9, true, true)]
    [InlineData(5, false, false)]
    [InlineData(9, false, false)]
    [InlineData(3, true, false)]
    [InlineData(-1, true, false)]
    public void TheCbtHook_RefusesOnlyActivationAndFocusOfAnOrbWindow(int code, bool isOrb, bool refused)
    {
        Assert.Equal(refused, WindowsFloatingWindowStyle.Refuses(code, isOrb));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void ASetFocusOnButtonDown_LikeAvalonias_NeitherActivatesNorFocusesTheOrb()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var window = TestWindow.CreateFocusingOnClick();
        new WindowsFloatingWindowStyle().MakeNonActivating(window.Handle);
        window.ShowNoActivate();
        Assert.True(WindowsFloatingWindowStyle.IsHooked);
        var calls = TestWindow.SetFocusCalls;

        // MAKELPARAM(20, 20) inside the window; MK_LBUTTON.
        window.Send(0x201, 1, (20 << 16) | 20);
        window.Send(0x202, 0, (20 << 16) | 20);

        Assert.Equal(calls + 1, TestWindow.SetFocusCalls);
        Assert.NotEqual(window.Handle, TestWindow.ActiveWindow);
        Assert.NotEqual(window.Handle, TestWindow.FocusWindow);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void TheHook_GoesWithTheLastOrbWindow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using (var window = TestWindow.Create())
        {
            new WindowsFloatingWindowStyle().MakeNonActivating(window.Handle);
            Assert.True(WindowsFloatingWindowStyle.IsHooked);
        }

        Assert.False(WindowsFloatingWindowStyle.IsHooked);
    }

    private static void AssertWindowAt(TestWindow window, int x, int y, bool expectOrb)
    {
        // The window manager may take a moment to settle the z-order of a window that was just shown (seen: the test failed now and then
        // with a foreign window found and the orb without WS_EX_TOPMOST, then passed on the next run), so it is asked again for a while.
        var found = TestWindow.WindowAt(x, y);
        for (var attempt = 0; attempt < 20 && (found == window.Handle) != expectOrb; attempt++)
        {
            Thread.Sleep(100);
            window.SetTopmost(true);
            found = TestWindow.WindowAt(x, y);
        }

        Assert.True(
            (found == window.Handle) == expectOrb,
            $"At ({x}, {y}) the orb {(expectOrb ? "should be" : "should not be")} the window found; found {TestWindow.Describe(found)} (the orb is {window.Handle}, extended style {window.ExtendedStyle:X}).");
    }

    private static void AssertNonActivating(nint style)
    {
        Assert.Equal(NoActivate, style & NoActivate);
        Assert.Equal(ToolWindow, style & ToolWindow);
        Assert.Equal((nint)0, style & AppWindow);
    }

    /// <summary>A plain popup window of the system's BUTTON class (it answers WM_NCHITTEST with HTCLIENT, unlike STATIC) on the test thread, standing in for the orb's Avalonia window.</summary>
    private sealed partial class TestWindow : IDisposable
    {
        private const uint WsPopup = 0x80000000;
        private const int SwShowNoActivate = 4;

        private const string FocusingClass = "MurmurOrbFocusTest";
        private static readonly Lock s_classLock = new();
        private static bool s_classRegistered;
        private static int s_setFocusCalls;

        private TestWindow(nint handle) => Handle = handle;

        public nint Handle { get; private set; }

        public nint ExtendedStyle => FloatingWindowNativeMethods.GetWindowLongPtrW(Handle, FloatingWindowNativeMethods.GwlExStyle);

        public nint Procedure => FloatingWindowNativeMethods.GetWindowLongPtrW(Handle, FloatingWindowNativeMethods.GwlpWndProc);

        public static TestWindow Create(int x = 0, int y = 0, int width = 52, int height = 52, uint exStyle = 0)
        {
            var handle = CreateWindowExW(exStyle, "BUTTON", "Murmur orb test", WsPopup, x, y, width, height, 0, 0, 0, 0);
            Assert.NotEqual((nint)0, handle);
            return new TestWindow(handle);
        }

        /// <summary>How many times the focusing window's procedure called <c>SetFocus</c> (it does on every left button down, like Avalonia).</summary>
        public static int SetFocusCalls => Volatile.Read(ref s_setFocusCalls);

        public static nint ActiveWindow => GetActiveWindow();

        public static nint FocusWindow => GetFocus();

        /// <summary>A popup of a class of its own whose procedure calls <c>SetFocus(hwnd)</c> on <c>WM_LBUTTONDOWN</c>, as Avalonia's <c>WindowImpl</c> does.</summary>
        public static unsafe TestWindow CreateFocusingOnClick()
        {
            lock (s_classLock)
            {
                if (!s_classRegistered)
                {
                    fixed (char* name = FocusingClass)
                    {
                        var windowClass = new WndClassW
                        {
                            WindowProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&FocusingProc,
                            ClassName = (nint)name,
                        };
                        Assert.NotEqual(0, RegisterClassW(in windowClass));
                    }

                    s_classRegistered = true;
                }
            }

            var handle = CreateWindowExW(0, FocusingClass, "Murmur orb focus test", WsPopup, 0, 0, 52, 52, 0, 0, 0, 0);
            Assert.NotEqual((nint)0, handle);
            return new TestWindow(handle);
        }

        [UnmanagedCallersOnly]
        private static nint FocusingProc(nint window, uint message, nint wParam, nint lParam)
        {
            if (message == 0x201)
            {
                Interlocked.Increment(ref s_setFocusCalls);
                SetFocus(window);
                return 0;
            }

            return DefWindowProcW(window, message, wParam, lParam);
        }

        public void ShowNoActivate() => ShowWindow(Handle, SwShowNoActivate);

        public void SetExtendedStyle(nint style) => FloatingWindowNativeMethods.SetWindowLongPtrW(Handle, FloatingWindowNativeMethods.GwlExStyle, style);

        public void SetTopmost(bool topmost) =>
            Assert.True(FloatingWindowNativeMethods.SetWindowPos(Handle, topmost ? -1 : -2, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010));

        /// <summary>The bounding box of the window's region as (left, top, right, bottom), or <c>null</c> without a region.</summary>
        public (int Left, int Top, int Right, int Bottom)? RegionBox
        {
            get
            {
                // SIMPLEREGION = 2, COMPLEXREGION = 3; ERROR = 0 (no region).
                var kind = GetWindowRgnBox(Handle, out var box);
                return kind is 2 or 3 ? (box.Left, box.Top, box.Right, box.Bottom) : null;
            }
        }

        public bool IsEnabled => FloatingWindowNativeMethods.IsWindowEnabled(Handle);

        public int Cloaked => FloatingWindowNativeMethods.DwmGetWindowAttribute(Handle, FloatingWindowNativeMethods.DwmwaCloaked, out var value, sizeof(int)) == 0 ? value : -1;

        public void Enable(bool enable) => FloatingWindowNativeMethods.EnableWindow(Handle, enable);

        public void Cloak(bool cloak) => Assert.Equal(0, FloatingWindowNativeMethods.DwmSetWindowAttribute(Handle, FloatingWindowNativeMethods.DwmwaCloak, cloak ? 1 : 0, sizeof(int)));

        /// <summary>The window's handle, class and rectangle, for a failure message.</summary>
        public static string Describe(nint window)
        {
            Span<ushort> className = stackalloc ushort[128];
            var length = GetClassNameW(window, ref className[0], className.Length);

            return GetWindowRect(window, out var rect)
                ? $"{window} ({MemoryMarshal.Cast<ushort, char>(className[..Math.Max(length, 0)])},{rect.Left},{rect.Top},{rect.Right},{rect.Bottom})"
                : $"{window}";
        }

        public static nint WindowAt(int x, int y) => WindowFromPoint(new Point(x, y));

        public nint Send(uint message, nint wParam, nint lParam) => SendMessageW(Handle, message, wParam, lParam);

        public void Dispose()
        {
            if (Handle != 0)
            {
                DestroyWindow(Handle);
                Handle = 0;
            }
        }

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindow(nint window, int command);

        [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
        private static partial nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

        /// <summary>winuser.h <c>WNDCLASSW</c>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct WndClassW
        {
            public uint Style;
            public nint WindowProc;
            public int ClassExtra;
            public int WindowExtra;
            public nint Instance;
            public nint Icon;
            public nint Cursor;
            public nint Background;
            public nint MenuName;
            public nint ClassName;
        }

        [LibraryImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
        private static partial ushort RegisterClassW(in WndClassW windowClass);

        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
        private static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

        [LibraryImport("user32.dll", EntryPoint = "SetFocus")]
        private static partial nint SetFocus(nint window);

        [LibraryImport("user32.dll", EntryPoint = "GetFocus")]
        private static partial nint GetFocus();

        [LibraryImport("user32.dll", EntryPoint = "GetActiveWindow")]
        private static partial nint GetActiveWindow();

        [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
        private static partial int GetClassNameW(nint window, ref ushort name, int capacity);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetWindowRect(nint window, out Rect rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct Point(int X, int Y);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowRgnBox")]
        private static partial int GetWindowRgnBox(nint window, out Rect box);

        [LibraryImport("user32.dll", EntryPoint = "WindowFromPoint")]
        private static partial nint WindowFromPoint(Point point);

        [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DestroyWindow(nint window);
    }
}
