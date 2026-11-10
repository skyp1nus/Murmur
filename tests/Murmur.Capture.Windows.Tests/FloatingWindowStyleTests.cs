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
        new WindowsFloatingWindowStyle().MakeNonActivating(0x7FFFFFF);

        Assert.False(WindowsFloatingWindowStyle.IsSubclassed(0x7FFFFFF));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void AfterShow_TheWindowIsNonActivatingAndAToolWindow()
    {
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
        using var window = TestWindow.Create();
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
        using var window = TestWindow.Create();
        window.SetExtendedStyle(NoActivate | ToolWindow);
        window.ShowNoActivate();

        window.SetExtendedStyle(AvaloniaStyle);

        Assert.Equal((nint)0, window.ExtendedStyle & NoActivate);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void WmMouseActivate_IsAnsweredWithMaNoActivate()
    {
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
        using var window = TestWindow.Create(x: 100, y: 100, width: 200, height: 124);
        var style = new WindowsFloatingWindowStyle();
        style.MakeNonActivating(window.Handle);
        window.ShowNoActivate();
        window.SetTopmost(true);

        style.SetInputRegion(window.Handle, [new(148, 36, 52, 52), new(0, 38, 150, 48)]);

        Assert.Equal((0, 36, 200, 88), window.RegionBox);
        Assert.Equal(window.Handle, TestWindow.WindowAt(100 + 170, 100 + 60));
        Assert.Equal(window.Handle, TestWindow.WindowAt(100 + 20, 100 + 60));
        Assert.NotEqual(window.Handle, TestWindow.WindowAt(100 + 20, 100 + 10));
        Assert.NotEqual(window.Handle, TestWindow.WindowAt(100 + 170, 100 + 110));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void AnEmptyInputRegion_GivesTheWholeWindowItsInputBack()
    {
        using var window = TestWindow.Create(x: 100, y: 100, width: 200, height: 124);
        var style = new WindowsFloatingWindowStyle();
        window.ShowNoActivate();
        style.SetInputRegion(window.Handle, [new(148, 36, 52, 52)]);

        style.SetInputRegion(window.Handle, []);

        Assert.Null(window.RegionBox);
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

        private TestWindow(nint handle) => Handle = handle;

        public nint Handle { get; private set; }

        public nint ExtendedStyle => FloatingWindowNativeMethods.GetWindowLongPtrW(Handle, FloatingWindowNativeMethods.GwlExStyle);

        public nint Procedure => FloatingWindowNativeMethods.GetWindowLongPtrW(Handle, FloatingWindowNativeMethods.GwlpWndProc);

        public static TestWindow Create(int x = 0, int y = 0, int width = 52, int height = 52)
        {
            var handle = CreateWindowExW(0, "BUTTON", "Murmur orb test", WsPopup, x, y, width, height, 0, 0, 0, 0);
            Assert.NotEqual((nint)0, handle);
            return new TestWindow(handle);
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
