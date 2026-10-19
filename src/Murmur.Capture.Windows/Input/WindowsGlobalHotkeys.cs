using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Input.Interop;
using Murmur.Core.Capture;
using Murmur.Core.Input;

namespace Murmur.Capture.Windows.Input;

/// <summary>
/// System-wide hotkeys through <c>RegisterHotKey</c> / <c>WM_HOTKEY</c> (no hook, no UI framework).
/// <list type="bullet">
/// <item>One background thread (<c>Murmur.GlobalHotkeys</c>) owns a message-only window and runs the message loop;
/// <c>RegisterHotKey</c> / <c>UnregisterHotKey</c> bind to that thread, so <see cref="Register"/> hands its request to
/// the thread (a private window message + a completion event) and returns the result synchronously. The thread starts
/// on the first Register that has something to register; constructing the object touches nothing native.</item>
/// <item>One id per action; <c>MOD_NOREPEAT</c> is always set, so holding the keys raises <see cref="Pressed"/> once.
/// Windows does not refuse one combination under two ids, so the same gesture for two actions is refused here:
/// <see cref="HotkeyRegistration.Taken"/>.</item>
/// <item>A failed Register leaves the action without a hotkey (the previous one is released first). Any
/// <c>RegisterHotKey</c> failure is <see cref="HotkeyRegistration.Taken"/> (reserved by the shell or held by another
/// program); the Win32 error stays in <see cref="LastError"/> and <see cref="LastFault"/>. If the window cannot be
/// created the result is <see cref="HotkeyRegistration.Unsupported"/>.</item>
/// <item><see cref="Pressed"/> is raised on the hotkey thread; a handler exception is swallowed (reported through
/// <see cref="Faulted"/>) and cannot stop the loop. A handler may call Register or Dispose: both detect that they run on
/// the hotkey thread and act inline. A handler that blocks delays every other hotkey and Register call.</item>
/// <item>Dispose unregisters everything, destroys the window, ends the loop and joins the thread (5 s timeout);
/// idempotent. Register after Dispose throws <see cref="ObjectDisposedException"/>.</item>
/// </list>
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </summary>
public sealed unsafe class WindowsGlobalHotkeys : IGlobalHotkeys
{
    internal const string ThreadName = "Murmur.GlobalHotkeys";

    private const string FaultSource = "global-hotkeys";
    private const string WindowClassName = "Murmur.GlobalHotkeys.MessageWindow";
    private const int ErrorTimeout = 1460;

    private const int Pending = 0;
    private const int Claimed = 1;
    private const int Abandoned = 2;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(5);

    private static readonly Lock ClassGate = new();
    private static bool s_classRegistered;

    private readonly Lock _gate = new();

    // Hotkey-thread state: touched only by the hotkey thread (WndProc, or inline calls made from a Pressed handler).
    private readonly HotkeyBook _book = new();
    private bool _windowDestroyed;

    // Shared state.
    private Thread? _thread;
    private volatile int _threadId;
    private nint _window;
    private int _disposed;
    private Request? _request;
    private int _lastError;
    private CaptureFault? _lastFault;

    public bool IsSupported => true;

    /// <summary>Raised on the hotkey thread, once per press.</summary>
    public event Action<HotkeyAction>? Pressed;

    /// <summary>
    /// Something unexpected happened (an unexpected <c>RegisterHotKey</c> error, a throwing <see cref="Pressed"/> handler,
    /// a thread that did not stop in time). Raised on any thread; exceptions from handlers are swallowed.
    /// </summary>
    public event Action<CaptureFault>? Faulted;

    /// <summary>The Win32 error of the last failed native call (<c>1409</c> = hotkey already registered), 0 if none.</summary>
    public int LastError => Volatile.Read(ref _lastError);

    /// <summary>The last fault reported through <see cref="Faulted"/>, or the last <c>RegisterHotKey</c> failure.</summary>
    public CaptureFault? LastFault => Volatile.Read(ref _lastFault);

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">The object was disposed.</exception>
    /// <exception cref="TimeoutException">The hotkey thread did not answer within 5 s (a blocked Pressed handler).</exception>
    public HotkeyRegistration Register(HotkeyAction action, string? gesture)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Not a hotkey action.");
        }

        HotkeyGesture? parsed = null;
        var invalid = false;
        if (gesture is not null)
        {
            if (HotkeyGesture.TryParse(gesture, out var value))
            {
                parsed = value;
            }
            else
            {
                invalid = true;
            }
        }

        if (OnHotkeyThread)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return Apply(action, parsed, invalid);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (parsed is null)
            {
                // Clearing (or an invalid text, which clears too): with no live thread nothing can be held.
                if (_thread is not { IsAlive: true })
                {
                    return invalid ? HotkeyRegistration.Invalid : HotkeyRegistration.Cleared;
                }
            }
            else if (!StartLocked())
            {
                return HotkeyRegistration.Unsupported;
            }

            return Dispatch(action, parsed, invalid);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (OnHotkeyThread)
        {
            // Disposed from a Pressed handler: do not take the gate (a Register from another thread may hold it while it
            // waits for this very thread) and do not join (a thread cannot join itself); the loop ends when the handler returns.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                InputNativeMethods.PostMessageW(_window, InputConstants.WmShutdown, 0, 0);
            }

            return;
        }

        Thread? thread;
        nint window;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            thread = _thread;
            window = _window;
        }

        if (thread is null)
        {
            return;
        }

        var posted = window != 0 && InputNativeMethods.PostMessageW(window, InputConstants.WmShutdown, 0, 0) != 0;
        if (!thread.Join(JoinTimeout))
        {
            Report($"The hotkey thread did not stop within {JoinTimeout.TotalSeconds:0} s (shutdown posted: {posted}).");
        }
    }

    /// <summary>True while the hotkey thread is running (it starts with the first Register that registers a gesture).</summary>
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

    private bool OnHotkeyThread
    {
        get
        {
            var id = _threadId;
            return id != 0 && id == Environment.CurrentManagedThreadId;
        }
    }

    // ---- caller side -----------------------------------------------------------------------------------------------

    private bool StartLocked()
    {
        if (_thread is { IsAlive: true })
        {
            return true;
        }

        var startup = new Startup();
        var thread = new Thread(() => ThreadMain(startup)) { IsBackground = true, Name = ThreadName };
        thread.Start();

        var ready = startup.Ready.Wait(StartTimeout);
        if (!ready && Interlocked.CompareExchange(ref startup.State, Abandoned, Pending) == Pending)
        {
            RecordError(ErrorTimeout, "The hotkey window was not created in time.");
            return false;
        }

        if (startup.Window == 0)
        {
            thread.Join(JoinTimeout);
            startup.Ready.Dispose();
            RecordError(startup.Error, "The hotkey message window could not be created");
            return false;
        }

        startup.Ready.Dispose();
        _thread = thread;
        return true;
    }

    private HotkeyRegistration Dispatch(HotkeyAction action, HotkeyGesture? gesture, bool invalid)
    {
        var window = _window;
        if (window == 0)
        {
            RecordError(0, "The hotkey thread has stopped");
            return HotkeyRegistration.Unsupported;
        }

        var request = new Request(action, gesture, invalid);
        Volatile.Write(ref _request, request);
        if (InputNativeMethods.PostMessageW(window, InputConstants.WmRegisterRequest, 0, 0) == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            Interlocked.CompareExchange(ref _request, null, request);
            RecordError(error, "The hotkey thread could not be reached");
            return HotkeyRegistration.Unsupported;
        }

        if (!request.Done.Wait(RequestTimeout))
        {
            if (Interlocked.CompareExchange(ref request.State, Abandoned, Pending) == Pending)
            {
                Interlocked.CompareExchange(ref _request, null, request);
                Report("The hotkey thread did not answer a Register call (is a Pressed handler blocking it?).");
                throw new TimeoutException("The global hotkey thread did not answer within 5 s.");
            }

            request.Done.Wait(); // The thread already took it: it finishes in microseconds.
        }

        var result = request.Result;
        request.Done.Dispose();
        return result;
    }

    // ---- hotkey thread -----------------------------------------------------------------------------------------------

    private void ThreadMain(Startup startup)
    {
        nint window = 0;
        var self = default(GCHandle);
        _windowDestroyed = false;
        try
        {
            window = CreateWindow(out var error);
            if (window == 0)
            {
                startup.Error = error;
                return;
            }

            self = GCHandle.Alloc(this);
            InputNativeMethods.SetWindowLongPtrW(window, InputConstants.GwlpUserData, GCHandle.ToIntPtr(self));
            _window = window;
            _threadId = Environment.CurrentManagedThreadId;
            startup.Window = window;
            if (Interlocked.CompareExchange(ref startup.State, Claimed, Pending) != Pending)
            {
                return; // The caller gave up waiting: tear down.
            }

            startup.Ready.Set();

            Msg message;
            while (InputNativeMethods.GetMessageW(&message, 0, 0, 0) > 0)
            {
                InputNativeMethods.DispatchMessageW(&message);
            }
        }
        catch (Exception ex)
        {
            Report($"The hotkey thread failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Cleanup(window);
            if (self.IsAllocated)
            {
                self.Free();
            }

            startup.Ready.Set();
        }
    }

    private void Cleanup(nint window)
    {
        if (window != 0)
        {
            UnregisterAll(window);
            if (!_windowDestroyed)
            {
                InputNativeMethods.DestroyWindow(window);
                _windowDestroyed = true;
            }
        }

        _threadId = 0;
        _window = 0;

        // A request that raced the shutdown must not wait for its timeout.
        var pending = Interlocked.Exchange(ref _request, null);
        if (pending is not null && Interlocked.CompareExchange(ref pending.State, Claimed, Pending) == Pending)
        {
            pending.Result = HotkeyRegistration.Unsupported;
            pending.Done.Set();
        }
    }

    private static nint CreateWindow(out int error)
    {
        fixed (char* className = WindowClassName)
        {
            if (!EnsureWindowClass(className, out error))
            {
                return 0;
            }

            var instance = InputNativeMethods.GetModuleHandleW(null);
            var window = InputNativeMethods.CreateWindowExW(
                0, className, null, 0, 0, 0, 0, 0, InputConstants.HwndMessage, 0, instance, 0);
            error = window == 0 ? Marshal.GetLastPInvokeError() : 0;
            return window;
        }
    }

    /// <summary>The window class is registered once per process (all instances share it) and never unregistered.</summary>
    private static bool EnsureWindowClass(char* className, out int error)
    {
        error = 0;
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
            if (InputNativeMethods.RegisterClassExW(&windowClass) == 0)
            {
                error = Marshal.GetLastPInvokeError();
                if (error != InputConstants.ErrorClassAlreadyExists)
                {
                    return false;
                }

                error = 0;
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
            if (data != 0 && GCHandle.FromIntPtr(data).Target is WindowsGlobalHotkeys owner)
            {
                switch (message)
                {
                    case InputConstants.WmHotkey:
                        owner.OnHotkey(wParam);
                        return 0;
                    case InputConstants.WmRegisterRequest:
                        owner.OnRegisterRequest();
                        return 0;
                    case InputConstants.WmShutdown:
                        owner.UnregisterAll(window);
                        InputNativeMethods.DestroyWindow(window);
                        return 0;
                    case InputConstants.WmDestroy:
                        owner._windowDestroyed = true;
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

    private void OnRegisterRequest()
    {
        var request = Interlocked.Exchange(ref _request, null);
        if (request is null || Interlocked.CompareExchange(ref request.State, Claimed, Pending) != Pending)
        {
            return; // Already abandoned by a caller that timed out.
        }

        try
        {
            request.Result = Apply(request.Action, request.Gesture, request.Invalid);
        }
        catch (Exception ex)
        {
            Report($"Register failed: {ex.GetType().Name}: {ex.Message}");
            request.Result = HotkeyRegistration.Taken;
        }
        finally
        {
            request.Done.Set();
        }
    }

    private HotkeyRegistration Apply(HotkeyAction action, HotkeyGesture? gesture, bool invalid)
    {
        var window = _window;
        var id = HotkeyBook.IdOf(action);
        if (_book.TryGet(action, out _))
        {
            InputNativeMethods.UnregisterHotKey(window, id);
            _book.Remove(action);
        }

        if (invalid)
        {
            return HotkeyRegistration.Invalid;
        }

        if (gesture is not { } wanted)
        {
            return HotkeyRegistration.Cleared;
        }

        if (_book.IsHeldByOther(action, wanted))
        {
            RecordError(InputConstants.ErrorHotkeyAlreadyRegistered, $"{wanted} is already used by another action of this app");
            return HotkeyRegistration.Taken;
        }

        var ok = InputNativeMethods.RegisterHotKey(
            window, id, HotkeyKeyMap.ToNativeModifiers(wanted.Modifiers), HotkeyKeyMap.ToVirtualKey(wanted.Key));
        if (ok == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            RecordError(error, $"RegisterHotKey({wanted}) failed", raise: error != InputConstants.ErrorHotkeyAlreadyRegistered);
            return HotkeyRegistration.Taken;
        }

        _book.Set(action, wanted);
        return HotkeyRegistration.Registered;
    }

    private void UnregisterAll(nint window)
    {
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            if (_book.TryGet(action, out _))
            {
                InputNativeMethods.UnregisterHotKey(window, HotkeyBook.IdOf(action));
                _book.Remove(action);
            }
        }
    }

    private void OnHotkey(nint id)
    {
        // A WM_HOTKEY queued before its hotkey was released is dropped.
        if (!HotkeyBook.TryActionOf(id, out var action) || !_book.TryGet(action, out _))
        {
            return;
        }

        var handlers = Pressed;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<HotkeyAction> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(action);
            }
            catch (Exception ex)
            {
                Report($"A Pressed handler for {action} threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ---- faults ------------------------------------------------------------------------------------------------------

    private void RecordError(int error, string what, bool raise = true)
    {
        Volatile.Write(ref _lastError, error);
        var message = $"{what} (Win32 error {error}).";
        if (raise)
        {
            Report(message);
        }
        else
        {
            Volatile.Write(ref _lastFault, new CaptureFault(FaultSource, message, IsFatal: false));
        }
    }

    private void Report(string message)
    {
        var fault = new CaptureFault(FaultSource, message, IsFatal: false);
        Volatile.Write(ref _lastFault, fault);
        try
        {
            Faulted?.Invoke(fault);
        }
        catch
        {
            // A broken fault handler must not break the hotkey thread.
        }
    }

    private sealed class Startup
    {
        public readonly ManualResetEventSlim Ready = new(false);
        public int State;
        public nint Window;
        public int Error;
    }

    private sealed class Request(HotkeyAction action, HotkeyGesture? gesture, bool invalid)
    {
        public readonly HotkeyAction Action = action;
        public readonly HotkeyGesture? Gesture = gesture;
        public readonly bool Invalid = invalid;
        public readonly ManualResetEventSlim Done = new(false);
        public int State;
        public HotkeyRegistration Result;
    }
}
