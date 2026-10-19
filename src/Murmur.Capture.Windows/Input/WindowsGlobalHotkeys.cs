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
/// the thread (a private window message + a completion monitor) and returns the result synchronously. The thread starts
/// on the first Register that has something to register; constructing the object touches nothing native.</item>
/// <item>One id per action; <c>MOD_NOREPEAT</c> is always set, so holding the keys raises <see cref="Pressed"/> once.
/// Windows does not refuse one combination under two ids, so the same gesture for two actions is refused here:
/// <see cref="HotkeyRegistration.Taken"/>.</item>
/// <item>A failed Register leaves the action without a hotkey (the previous one is released first). Any
/// <c>RegisterHotKey</c> failure is <see cref="HotkeyRegistration.Taken"/> (reserved by the shell or held by another
/// program); the Win32 error stays in <see cref="LastError"/> and <see cref="LastFault"/>. If the window cannot be
/// created the result is <see cref="HotkeyRegistration.Unsupported"/>. If the hotkey thread does not answer within 5 s
/// (a blocked <see cref="Pressed"/> handler) the result is <see cref="HotkeyRegistration.Taken"/> with
/// <see cref="LastError"/> 1460 (<c>ERROR_TIMEOUT</c>); the request is then abandoned, but one that the thread had
/// already started may still have changed the registration, so the old gesture may still be registered.</item>
/// <item><see cref="Pressed"/> is raised on the hotkey thread; a handler exception is swallowed (reported through
/// <see cref="Faulted"/>) and cannot stop the loop. A handler may call Register or Dispose: both detect that they run on
/// the hotkey thread and act inline. A handler that blocks delays every other hotkey and Register call.</item>
/// <item>Dispose unregisters everything, destroys the window, ends the loop and joins the thread (5 s timeout);
/// idempotent; every external Dispose call waits for the thread, also when a Pressed handler already disposed inline.
/// Register after Dispose throws <see cref="ObjectDisposedException"/>.</item>
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
    private CaptureFault? _deferredFault;

    // Shared state.
    private Thread? _thread;
    private int _threadId;
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
            try
            {
                return Apply(action, parsed, invalid);
            }
            finally
            {
                FlushDeferredFault();
            }
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
            // waits for this very thread) and do not join (a thread cannot join itself); the loop ends when the handler
            // returns, and a later external Dispose joins it.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                InputNativeMethods.PostMessageW(Volatile.Read(ref _window), InputConstants.WmShutdown, 0, 0);
            }

            return;
        }

        Thread? thread;
        bool first;
        lock (_gate)
        {
            first = Interlocked.Exchange(ref _disposed, 1) == 0;
            thread = _thread;
            if (first && thread is not null)
            {
                var window = Volatile.Read(ref _window);
                if (window != 0)
                {
                    InputNativeMethods.PostMessageW(window, InputConstants.WmShutdown, 0, 0);
                }
            }
        }

        // Every external call joins, also when an earlier inline Dispose (or an earlier call) already posted the shutdown.
        if (thread is { IsAlive: true } && thread != Thread.CurrentThread && !thread.Join(JoinTimeout))
        {
            Report($"The hotkey thread did not stop within {JoinTimeout.TotalSeconds:0} s.");
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
            var id = Volatile.Read(ref _threadId);
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
            RecordError(startup.Error, "The hotkey message window could not be created");
            return false;
        }

        _thread = thread;
        return true;
    }

    private HotkeyRegistration Dispatch(HotkeyAction action, HotkeyGesture? gesture, bool invalid)
    {
        var window = Volatile.Read(ref _window);
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
                RecordError(ErrorTimeout, "The hotkey thread did not answer a Register call (is a Pressed handler blocking it?)");
                return HotkeyRegistration.Taken;
            }

            // The thread already took it; it only has to finish the one call.
            if (!request.Done.Wait(RequestTimeout))
            {
                RecordError(ErrorTimeout, "The hotkey thread did not finish a Register call");
                return HotkeyRegistration.Taken;
            }
        }

        return request.Result;
    }

    // ---- hotkey thread -----------------------------------------------------------------------------------------------

    private void ThreadMain(Startup startup)
    {
        nint window = 0;
        var published = false;
        var self = default(GCHandle);
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
            startup.Window = window;
            if (Interlocked.CompareExchange(ref startup.State, Claimed, Pending) != Pending)
            {
                return; // The caller gave up waiting: tear down without touching the shared state.
            }

            // Only a thread the caller accepted may publish itself (an abandoned one must not clobber a newer thread).
            published = true;
            Volatile.Write(ref _window, window);
            Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
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
            Cleanup(window, published);
            if (self.IsAllocated)
            {
                self.Free();
            }

            startup.Ready.Set();
        }
    }

    private void Cleanup(nint window, bool published)
    {
        if (window != 0)
        {
            if (published)
            {
                UnregisterAll(window);
            }

            // Fails harmlessly when the window is already gone (and cannot hit another thread's window).
            InputNativeMethods.DestroyWindow(window);
        }

        if (!published)
        {
            return;
        }

        Interlocked.CompareExchange(ref _window, 0, window);
        Interlocked.CompareExchange(ref _threadId, 0, Environment.CurrentManagedThreadId);

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
            Defer(new CaptureFault(FaultSource, $"Register failed: {ex.GetType().Name}: {ex.Message}", IsFatal: false));
            request.Result = HotkeyRegistration.Taken;
        }
        finally
        {
            request.Done.Set();
        }

        FlushDeferredFault(); // After the caller was released: a Faulted handler must not delay Register.
    }

    private HotkeyRegistration Apply(HotkeyAction action, HotkeyGesture? gesture, bool invalid)
    {
        var window = Volatile.Read(ref _window);
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
            RecordError(InputConstants.ErrorHotkeyAlreadyRegistered, $"{wanted} is already used by another action of this app", raise: false);
            return HotkeyRegistration.Taken;
        }

        var ok = InputNativeMethods.RegisterHotKey(
            window, id, HotkeyKeyMap.ToNativeModifiers(wanted.Modifiers), HotkeyKeyMap.ToVirtualKey(wanted.Key));
        if (ok == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            RecordError(error, $"RegisterHotKey({wanted}) failed", raise: error != InputConstants.ErrorHotkeyAlreadyRegistered, deferred: true);
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

    /// <summary>
    /// Keeps the error for <see cref="LastError"/> / <see cref="LastFault"/> and, when <paramref name="raise"/>, raises
    /// <see cref="Faulted"/> - at once, or (<paramref name="deferred"/>, on the hotkey thread) after the waiting caller was released.
    /// </summary>
    private void RecordError(int error, string what, bool raise = true, bool deferred = false)
    {
        Volatile.Write(ref _lastError, error);
        var fault = new CaptureFault(FaultSource, $"{what} (Win32 error {error}).", IsFatal: false);
        Volatile.Write(ref _lastFault, fault);
        if (!raise)
        {
            return;
        }

        if (deferred)
        {
            _deferredFault = fault;
        }
        else
        {
            Raise(fault);
        }
    }

    private void Defer(CaptureFault fault)
    {
        Volatile.Write(ref _lastFault, fault);
        _deferredFault = fault;
    }

    private void FlushDeferredFault()
    {
        var fault = _deferredFault;
        if (fault is not null)
        {
            _deferredFault = null;
            Raise(fault);
        }
    }

    private void Report(string message)
    {
        var fault = new CaptureFault(FaultSource, message, IsFatal: false);
        Volatile.Write(ref _lastFault, fault);
        Raise(fault);
    }

    private void Raise(CaptureFault fault)
    {
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
        public readonly Completion Ready = new();
        public int State;
        public nint Window;
        public int Error;
    }

    private sealed class Request(HotkeyAction action, HotkeyGesture? gesture, bool invalid)
    {
        public readonly HotkeyAction Action = action;
        public readonly HotkeyGesture? Gesture = gesture;
        public readonly bool Invalid = invalid;
        public readonly Completion Done = new();
        public int State;
        public HotkeyRegistration Result;
    }

    /// <summary>
    /// A one-shot signal on a monitor: no kernel handle and nothing to dispose, so the signalling thread can never race a
    /// disposing waiter (the reason this is not a <c>ManualResetEventSlim</c>). Writes made before <see cref="Set"/> are
    /// visible after a successful <see cref="Wait"/>.
    /// </summary>
    private sealed class Completion
    {
        private readonly object _sync = new();
        private bool _set;

        public void Set()
        {
            lock (_sync)
            {
                _set = true;
                Monitor.PulseAll(_sync);
            }
        }

        public bool Wait(TimeSpan timeout)
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            lock (_sync)
            {
                while (!_set)
                {
                    var left = deadline - Environment.TickCount64;
                    if (left <= 0)
                    {
                        return false;
                    }

                    Monitor.Wait(_sync, (int)left);
                }

                return true;
            }
        }
    }
}
