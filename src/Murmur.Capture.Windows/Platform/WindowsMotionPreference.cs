using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Input.Interop;
using Murmur.Core.Platform;

namespace Murmur.Capture.Windows.Platform;

/// <summary>
/// <see cref="IMotionPreference"/> on Windows: <c>ReduceMotion = !SPI_GETCLIENTAREAANIMATION</c> (Settings → Accessibility →
/// Visual effects → Animation effects).
/// <list type="bullet">
/// <item><see cref="ReduceMotion"/> asks the system every time (one cheap user32 call, never blocks), so it is always current.</item>
/// <item><see cref="Changed"/> needs a window to hear <c>WM_SETTINGCHANGE</c>. That message is broadcast to top-level windows
/// only, never to message-only ones, so the hotkeys' window cannot carry it: this class owns a hidden, never shown top-level
/// window on its own background thread (<c>Murmur.MotionPreference</c>). The thread starts on the first subscriber, not at
/// construction. On every <c>WM_SETTINGCHANGE</c> the value is read again and <see cref="Changed"/> is raised, on that thread,
/// only when it differs from the last one seen.</item>
/// <item>A handler exception is swallowed (the other handlers still run). <see cref="Dispose"/> ends the thread and is safe to call more than once.</item>
/// </list>
/// </summary>
internal sealed unsafe class WindowsMotionPreference : IMotionPreference, IDisposable
{
    internal const string ThreadName = "Murmur.MotionPreference";

    private const string WindowClassName = "Murmur.MotionPreferenceWindow";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);
    private static readonly Lock ClassGate = new();
    private static bool s_classRegistered;

    private readonly Func<bool> _animationsEnabled;
    private readonly Lock _gate = new();
    private Action? _changed;
    private Thread? _thread;
    private nint _window;
    private volatile bool _last;
    private bool _disposed;

    /// <summary>Reads the real system setting.</summary>
    public WindowsMotionPreference()
        : this(MotionNativeMethods.ClientAreaAnimationEnabled)
    {
    }

    /// <summary>Reads the setting through <paramref name="animationsEnabled"/> (tests replace the system call).</summary>
    internal WindowsMotionPreference(Func<bool> animationsEnabled)
    {
        ArgumentNullException.ThrowIfNull(animationsEnabled);
        _animationsEnabled = animationsEnabled;
    }

    public bool ReduceMotion => !_animationsEnabled();

    public event Action? Changed
    {
        add
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _changed += value;
                StartLocked();
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    /// <summary>The handle of the hidden window, 0 while there is none (for tests).</summary>
    internal nint Window => Volatile.Read(ref _window);

    /// <summary>True while the window thread runs (for tests).</summary>
    internal bool HasLiveThread
    {
        get
        {
            lock (_gate)
            {
                return _thread is { IsAlive: true };
            }
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _changed = null;
            thread = _thread;
            _thread = null;
        }

        var window = Volatile.Read(ref _window);
        if (window != 0)
        {
            InputNativeMethods.PostMessageW(window, InputConstants.WmShutdown, 0, 0);
        }

        if (thread is { IsAlive: true } && thread != Thread.CurrentThread)
        {
            thread.Join(JoinTimeout);
        }
    }

    private void StartLocked()
    {
        if (_thread is { IsAlive: true })
        {
            return;
        }

        // The value the thread starts from: a change after this point raises Changed.
        _last = ReduceMotion;
        // Not disposed: the thread sets it once more when it ends, long after this method returned (it holds no kernel handle).
        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() => ThreadMain(ready)) { IsBackground = true, Name = ThreadName };
        thread.Start();
        if (ready.Wait(StartTimeout))
        {
            _thread = thread;
        }
    }

    private void ThreadMain(ManualResetEventSlim ready)
    {
        nint window = 0;
        var self = default(GCHandle);
        try
        {
            window = CreateWindow();
            if (window == 0)
            {
                return;
            }

            self = GCHandle.Alloc(this);
            InputNativeMethods.SetWindowLongPtrW(window, InputConstants.GwlpUserData, GCHandle.ToIntPtr(self));
            Volatile.Write(ref _window, window);
            ready.Set();

            Msg message;
            while (InputNativeMethods.GetMessageW(&message, 0, 0, 0) > 0)
            {
                InputNativeMethods.DispatchMessageW(&message);
            }
        }
        catch
        {
            // A broken window means no change notifications; ReduceMotion keeps answering from the system.
        }
        finally
        {
            if (window != 0)
            {
                InputNativeMethods.DestroyWindow(window);
                Interlocked.CompareExchange(ref _window, 0, window);
            }

            if (self.IsAllocated)
            {
                self.Free();
            }

            ready.Set();
        }
    }

    private static nint CreateWindow()
    {
        fixed (char* className = WindowClassName)
        {
            if (!EnsureWindowClass(className))
            {
                return 0;
            }

            // Parent 0, not HWND_MESSAGE: broadcasts such as WM_SETTINGCHANGE reach top-level windows only. Never shown.
            var instance = InputNativeMethods.GetModuleHandleW(null);
            return InputNativeMethods.CreateWindowExW(0, className, null, 0, 0, 0, 0, 0, 0, 0, instance, 0);
        }
    }

    private static bool EnsureWindowClass(char* className)
    {
        lock (ClassGate)
        {
            if (s_classRegistered)
            {
                return true;
            }

            var windowClass = new WndClassExW
            {
                Size = (uint)sizeof(WndClassExW),
                WindowProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WindowProc,
                Instance = InputNativeMethods.GetModuleHandleW(null),
                ClassName = (nint)className,
            };
            if (InputNativeMethods.RegisterClassExW(&windowClass) == 0
                && Marshal.GetLastPInvokeError() != InputConstants.ErrorClassAlreadyExists)
            {
                return false;
            }

            s_classRegistered = true;
            return true;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            var data = InputNativeMethods.GetWindowLongPtrW(window, InputConstants.GwlpUserData);
            if (data != 0 && GCHandle.FromIntPtr(data).Target is WindowsMotionPreference owner)
            {
                switch (message)
                {
                    case MotionNativeMethods.WmSettingChange:
                        owner.OnSettingChange();
                        break;
                    case InputConstants.WmShutdown:
                        InputNativeMethods.DestroyWindow(window);
                        return 0;
                    case InputConstants.WmDestroy:
                        InputNativeMethods.PostQuitMessage(0);
                        return 0;
                }
            }
        }
        catch
        {
            // An exception must never unwind through the native frame.
        }

        return InputNativeMethods.DefWindowProcW(window, message, wParam, lParam);
    }

    private void OnSettingChange()
    {
        var reduce = ReduceMotion;
        if (reduce == _last)
        {
            return;
        }

        _last = reduce;
        Action? handlers;
        lock (_gate)
        {
            handlers = _changed;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch
            {
                // A subscriber's failure reaches neither the other subscribers nor the window's next change.
            }
        }
    }
}
