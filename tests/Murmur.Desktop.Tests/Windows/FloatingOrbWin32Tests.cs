using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;
using Murmur.App.Views;
using Murmur.Capture.Windows;

namespace Murmur.Desktop.Tests.Windows;

/// <summary>
/// The floating orb's real window (#46) on the Win32 platform of Avalonia, with the platform style of the app: the extended style
/// survives Avalonia's own rewrites (Show, a Topmost toggle), and a click, which makes Avalonia call <c>SetFocus</c>, activates
/// nothing. Avalonia runs once per process, on a thread of its own.
/// </summary>
public sealed partial class FloatingOrbWin32Tests
{
    private const nint NoActivate = 0x08000000;
    private const nint ToolWindow = 0x00000080;

    [Fact]
    [Trait("Category", "Windows")]
    public void ARealOrbWindow_KeepsItsStyleThroughShowAndTopmost_AndAClickActivatesNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Win32 window runs only on Windows.");
        Result? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = Run();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(1)), "The Avalonia thread did not finish.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        var r = result!.Value;
        Assert.Equal(NoActivate | ToolWindow, r.AfterShow & (NoActivate | ToolWindow));
        Assert.Equal(NoActivate | ToolWindow, r.AfterTopmost & (NoActivate | ToolWindow));
        Assert.NotEqual(r.Handle, r.Active);
        Assert.NotEqual(r.Handle, r.Focus);
        Assert.NotEqual(r.Handle, r.Foreground);
    }

    private readonly record struct Result(nint Handle, nint AfterShow, nint AfterTopmost, nint Active, nint Focus, nint Foreground);

    private static Result Run()
    {
        AppBuilder.Configure<Murmur.App.App>().UseWin32().UseSkia().WithInterFont().SetupWithoutStarting();
        var window = new FloatingOrbWindow();
        var handle = window.TryGetPlatformHandle()!.Handle;
        var style = WindowsCapture.CreateFloatingWindowStyle();

        // What FloatingOrbController does: once the window exists, and again after the show.
        style.MakeNonActivating(handle);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        style.MakeNonActivating(handle);
        var afterShow = GetWindowLongPtrW(handle, -20);

        window.Topmost = false;
        Dispatcher.UIThread.RunJobs();
        window.Topmost = true;
        Dispatcher.UIThread.RunJobs();
        var afterTopmost = GetWindowLongPtrW(handle, -20);

        // A click on the circle (4 px margin + 148 px pill slot + 22; 4 + 36 px chip slot + 22), in client pixels.
        var scale = window.RenderScaling;
        var x = (int)((4 + 148 + 22) * scale);
        var y = (int)((4 + 36 + 22) * scale);
        var point = (y << 16) | x;
        SendMessageW(handle, 0x201, 1, point);
        Dispatcher.UIThread.RunJobs();
        SendMessageW(handle, 0x202, 0, point);
        Dispatcher.UIThread.RunJobs();

        var result = new Result(handle, afterShow, afterTopmost, GetActiveWindow(), GetFocus(), GetForegroundWindow());
        window.Close();
        Dispatcher.UIThread.RunJobs();
        return result;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetActiveWindow")]
    private static partial nint GetActiveWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetFocus")]
    private static partial nint GetFocus();

    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static partial nint GetForegroundWindow();
}
