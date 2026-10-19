using Murmur.App.Diagnostics;
using Murmur.App.Services;

namespace Murmur.Desktop;

/// <summary>
/// The OS primitives behind <see cref="SingleInstance"/>: a named mutex that says who was first and a named event that a
/// later start sets to wake the first one. A seam, so the logic is tested on Linux with a fake.
/// </summary>
internal interface IInstanceChannel : IDisposable
{
    /// <summary>This process created the mutex: no other Murmur is running.</summary>
    bool IsFirst { get; }

    /// <summary>Wakes the first instance (a later start calls this before it exits).</summary>
    void SignalFirst();

    /// <summary>Blocks until the event is set (<see langword="true"/>) or <paramref name="cancellationToken"/> is cancelled (<see langword="false"/>).</summary>
    bool Wait(CancellationToken cancellationToken);
}

/// <summary>
/// One Murmur per user session. The first start holds the mutex <see cref="MutexName"/> for the whole run (the installer's
/// <c>AppMutex</c> names the same mutex) and waits for <see cref="EventName"/> on a background thread; a second start sets
/// that event and ends with exit code 0 before any UI exists. The first instance then raises <see cref="Requested"/>, which
/// the app answers by bringing its window to the front.
/// </summary>
/// <remarks>
/// Diagnostic runs (<c>--startup-probe</c>, <c>--autorecord</c>) skip the check altogether: they neither exit silently because
/// a leftover instance of an earlier step is still alive (CI start-up and capture checks, <c>scripts/verify-local.ps1</c>), nor
/// wake the user's own running Murmur. A channel that cannot be opened (access denied, not Windows) also means "no check":
/// the app starts rather than refuses.
/// </remarks>
internal sealed class SingleInstance : IActivationRequests, IDisposable
{
    public const string MutexName = "MurmurSingleInstance";
    public const string EventName = "MurmurShowApp";

    private readonly IInstanceChannel? _channel;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _listener;

    private SingleInstance(IInstanceChannel? channel, bool isPrimary)
    {
        _channel = channel;
        IsPrimary = isPrimary;
    }

    /// <summary>This process runs the app. <see langword="false"/>: another one does, it has been told, and this one should exit with 0.</summary>
    public bool IsPrimary { get; }

    /// <summary>Raised on the listener thread when a later start asked for the window.</summary>
    public event Action? Requested;

    /// <summary>The command line is a diagnostic run, which never takes part in the single-instance check.</summary>
    public static bool IsDiagnostic(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (var arg in args)
        {
            if (string.Equals(arg, StartupProbe.Flag, StringComparison.Ordinal)
                || string.Equals(arg, AutoRecordOptions.Flag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The channel of the running OS: the named mutex and event on Windows, none elsewhere.</summary>
    public static IInstanceChannel? OpenSystemChannel() => OperatingSystem.IsWindows() ? WindowsInstanceChannel.Open() : null;

    /// <summary>
    /// Decides whether this process is the first Murmur. <paramref name="open"/> creates the channel (called once, never for a
    /// diagnostic run); <see langword="null"/> or an exception means there is no check.
    /// </summary>
    public static SingleInstance Enter(IReadOnlyList<string> args, Func<IInstanceChannel?> open)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(open);
        if (IsDiagnostic(args))
        {
            return new SingleInstance(null, isPrimary: true);
        }

        IInstanceChannel? channel;
        try
        {
            channel = open();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Single-instance check unavailable: {ex.Message}");
            return new SingleInstance(null, isPrimary: true);
        }

        if (channel is null)
        {
            return new SingleInstance(null, isPrimary: true);
        }

        if (!channel.IsFirst)
        {
            try
            {
                channel.SignalFirst();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not wake the running Murmur: {ex.Message}");
            }

            channel.Dispose();
            return new SingleInstance(null, isPrimary: false);
        }

        var instance = new SingleInstance(channel, isPrimary: true);
        instance.StartListening();
        return instance;
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_listener is { } listener && listener != Thread.CurrentThread)
        {
            listener.Join(TimeSpan.FromSeconds(2));
        }

        _channel?.Dispose();
        _stop.Dispose();
    }

    private void StartListening()
    {
        _listener = new Thread(Listen) { IsBackground = true, Name = "Murmur.SingleInstance" };
        _listener.Start();
    }

    private void Listen()
    {
        var channel = _channel!;
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            bool signaled;
            try
            {
                signaled = channel.Wait(token);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (!signaled)
            {
                return;
            }

            try
            {
                Requested?.Invoke();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Showing the window for a second start failed: {ex.Message}");
            }
        }
    }
}

/// <summary>The named mutex and auto-reset event of the current user session (no <c>Global\</c> prefix: one Murmur per session).</summary>
internal sealed class WindowsInstanceChannel : IInstanceChannel
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _event;

    private WindowsInstanceChannel(Mutex mutex, EventWaitHandle signal, bool isFirst)
    {
        _mutex = mutex;
        _event = signal;
        IsFirst = isFirst;
    }

    public bool IsFirst { get; }

    /// <summary>Creates, or opens when it already exists, the mutex and the event.</summary>
    public static WindowsInstanceChannel Open()
    {
        var mutex = new Mutex(initiallyOwned: false, SingleInstance.MutexName, out var created);
        try
        {
            var signal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, SingleInstance.EventName);
            return new WindowsInstanceChannel(mutex, signal, created);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void SignalFirst() => _event.Set();

    public bool Wait(CancellationToken cancellationToken) =>
        WaitHandle.WaitAny([_event, cancellationToken.WaitHandle]) == 0;

    public void Dispose()
    {
        _event.Dispose();
        _mutex.Dispose();
    }
}
