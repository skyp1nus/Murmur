using System.Runtime.InteropServices;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// A small topmost window that repaints with new content on every desktop composition (DwmFlush), so
/// Windows.Graphics.Capture delivers a frame per display refresh: "moving content" for the screen recorder tests,
/// independent of what else is on screen. It never takes focus (WS_EX_NOACTIVATE) and lives on its own thread with its
/// own message loop. Silent: GDI only.
/// </summary>
internal sealed unsafe partial class AnimatedWindow : IDisposable
{
    private const uint WsPopup = 0x8000_0000;
    private const uint WsVisible = 0x1000_0000;
    private const uint WsExTopmost = 0x0000_0008;
    private const uint WsExToolWindow = 0x0000_0080;
    private const uint WsExNoActivate = 0x0800_0000;
    private const int SwShowNoActivate = 4;
    private const uint PmRemove = 1;
    private const uint PatCopy = 0x00F0_0021;
    private const int DcBrush = 18;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly int _x;
    private readonly int _y;
    private readonly int _width;
    private readonly int _height;
    private volatile bool _stop;
    private long _frames;
    private long _threadCpuTicks;
    private string? _error;

    private AnimatedWindow(int x, int y, int width, int height)
    {
        (_x, _y, _width, _height) = (x, y, width, height);
        _thread = new Thread(Run) { IsBackground = true, Name = "Murmur test animated window" };
    }

    /// <summary>Repaints so far (one per composition pass).</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>CPU time (100 ns) the window thread used so far: subtract it from the test process's CPU time.</summary>
    public long ThreadCpuTicks => Interlocked.Read(ref _threadCpuTicks);

    /// <summary>Opens the window at the given screen position and waits until it painted a few frames.</summary>
    public static AnimatedWindow Start(int x, int y, int width, int height)
    {
        var window = new AnimatedWindow(x, y, width, height);
        window._thread.Start();
        if (!window._ready.Wait(TimeSpan.FromSeconds(10)) || window._error is not null)
        {
            window.Dispose();
            throw new InvalidOperationException("The animated test window did not start: " + (window._error ?? "timeout"));
        }

        return window;
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(10));
        _ready.Dispose();
    }

    private void Run()
    {
        var className = "MurmurTestAnimatedWindow" + Environment.CurrentManagedThreadId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var instance = GetModuleHandleW(null);
        nint window = 0, dc = 0;
        fixed (char* pClass = className)
        {
            try
            {
                var wc = new WndClassExW
                {
                    Size = (uint)sizeof(WndClassExW),
                    WndProc = &WndProc,
                    Instance = instance,
                    ClassName = pClass,
                };
                if (RegisterClassExW(&wc) == 0)
                {
                    _error = "RegisterClassExW failed: " + Marshal.GetLastPInvokeError();
                    return;
                }

                window = CreateWindowExW(WsExTopmost | WsExToolWindow | WsExNoActivate, pClass, pClass, WsPopup | WsVisible, _x, _y, _width, _height, 0, 0, instance, 0);
                if (window == 0)
                {
                    _error = "CreateWindowExW failed: " + Marshal.GetLastPInvokeError();
                    return;
                }

                ShowWindow(window, SwShowNoActivate);
                dc = GetDC(window);
                SelectObject(dc, GetStockObject(DcBrush));
                Msg msg;
                for (var frame = 0L; !_stop; frame++)
                {
                    while (PeekMessageW(&msg, 0, 0, 0, PmRemove) != 0)
                    {
                        TranslateMessage(&msg);
                        DispatchMessageW(&msg);
                    }

                    // A new background colour and a bar that moves 8 px per frame: every composition has new pixels.
                    var shade = (uint)(frame * 5 % 256);
                    SetDCBrushColor(dc, shade | ((255 - shade) << 8) | ((shade / 2) << 16));
                    PatBlt(dc, 0, 0, _width, _height, PatCopy);
                    SetDCBrushColor(dc, 0x00FF_FFFF);
                    PatBlt(dc, (int)(frame * 8 % _width), 0, 24, _height, PatCopy);
                    GdiFlush();
                    DwmFlush();
                    Interlocked.Increment(ref _frames);
                    if (frame == 10)
                    {
                        _ready.Set();
                    }

                    if (frame % 16 == 0)
                    {
                        UpdateThreadCpu();
                    }
                }
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
            finally
            {
                UpdateThreadCpu();
                if (dc != 0)
                {
                    ReleaseDC(window, dc);
                }

                if (window != 0)
                {
                    DestroyWindow(window);
                }

                UnregisterClassW(pClass, instance);
                _ready.Set();
            }
        }
    }

    private void UpdateThreadCpu()
    {
        long creation, exit, kernel, user;
        if (GetThreadTimes(GetCurrentThread(), &creation, &exit, &kernel, &user) != 0)
        {
            Interlocked.Exchange(ref _threadCpuTicks, kernel + user);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint window, uint message, nint wParam, nint lParam) => DefWindowProcW(window, message, wParam, lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassExW
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Window;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(char* moduleName);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    private static partial int GetThreadTimes(nint thread, long* creation, long* exit, long* kernel, long* user);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(WndClassExW* windowClass);

    [LibraryImport("user32.dll")]
    private static partial int UnregisterClassW(char* className, nint instance);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial int DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int PeekMessageW(Msg* message, nint window, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll")]
    private static partial int TranslateMessage(Msg* message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(Msg* message);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint GetStockObject(int index);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint gdiObject);

    [LibraryImport("gdi32.dll")]
    private static partial uint SetDCBrushColor(nint dc, uint color);

    [LibraryImport("gdi32.dll")]
    private static partial int PatBlt(nint dc, int x, int y, int width, int height, uint rop);

    [LibraryImport("gdi32.dll")]
    private static partial int GdiFlush();

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmFlush();
}
