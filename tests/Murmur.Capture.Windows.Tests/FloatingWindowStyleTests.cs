using Murmur.Capture.Windows.Platform;

namespace Murmur.Capture.Windows.Tests;

/// <summary>The extended-style maths of the floating orb's window (#46), on any OS, and the Win32 call itself on Windows.</summary>
public sealed class FloatingWindowStyleTests
{
    private const nint NoActivate = 0x08000000;
    private const nint ToolWindow = 0x00000080;
    private const nint AppWindow = 0x00040000;
    private const nint TopMost = 0x00000008;

    [Fact]
    public void TheValues_AreTheOnesOfWinuserH()
    {
        Assert.Equal(-20, FloatingWindowNativeMethods.GwlExStyle);
        Assert.Equal(NoActivate, FloatingWindowNativeMethods.WsExNoActivate);
        Assert.Equal(ToolWindow, FloatingWindowNativeMethods.WsExToolWindow);
        Assert.Equal(AppWindow, FloatingWindowNativeMethods.WsExAppWindow);
        Assert.Equal(0x0037u, WindowsFloatingWindowStyle.FrameChangeOnly);
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
    public void ANullHandle_IsIgnored()
    {
        new WindowsFloatingWindowStyle().MakeNonActivating(0);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void AHandleThatIsNotAWindow_DoesNotThrow()
    {
        new WindowsFloatingWindowStyle().MakeNonActivating(0x7FFFFFF);
    }
}
