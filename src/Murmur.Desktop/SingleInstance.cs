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

    /// <summary>Asks the first instance to show its window (a later start calls this). Clears an old, unclaimed answer first.</summary>
    void SignalFirst();

    /// <summary>The first instance's answer: its window was brought to the front. Any thread.</summary>
    void Acknowledge();

    /// <summary>Waits up to <paramref name="timeout"/> for the first instance's answer to <see cref="SignalFirst"/>.</summary>
    bool WaitAcknowledged(TimeSpan timeout);

    /// <summary>Blocks until the event is set (<see langword="true"/>) or <paramref name="cancellationToken"/> is cancelled (<see langword="false"/>).</summary>
    bool Wait(CancellationToken cancellationToken);
}

/// <summary>
/// One Murmur per user session. The first start holds the mutex <see cref="MutexName"/> for the whole run (the installer's
/// <c>AppMutex</c> names the same mutex) and waits for <see cref="EventName"/> on a background thread; a second start sets
/// that event and ends with exit code 0 before any UI exists. The first instance raises <see cref="Requested"/>, the app
/// brings its window to the front and calls <see cref="Acknowledge"/>, which is what lets the second start exit. A first
/// instance that does not answer within <see cref="SingleInstanceTiming.AckTimeout"/> is on its way out (its window is gone or
/// closing): the second start then tries to become the first one every <see cref="SingleInstanceTiming.RetryInterval"/> for up
/// to <see cref="SingleInstanceTiming.RetryWindow"/> and starts normally once it can. The names have no <c>Global\</c> prefix:
/// one Murmur per user session (a per-user install).
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
    public const string AckEventName = "MurmurShowAppAck";

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
    /// Decides whether this process is the first Murmur. <paramref name="open"/> creates the channel (never for a diagnostic
    /// run, and again for every retry of a second start); <see langword="null"/> or an exception means there is no check. A
    /// second start blocks here until the first instance has answered, or until it could take over, or gave up
    /// (<see cref="IsPrimary"/> <see langword="false"/> either way when the first is still there).
    /// </summary>
    public static SingleInstance Enter(IReadOnlyList<string> args, Func<IInstanceChannel?> open, SingleInstanceTiming? timing = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(open);
        var wait = timing ?? SingleInstanceTiming.Default;
        if (IsDiagnostic(args))
        {
            return new SingleInstance(null, isPrimary: true);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var asked = false;
        var nextAsk = TimeSpan.Zero;
        while (true)
        {
            var channel = TryOpen(open);
            if (channel is null)
            {
                return new SingleInstance(null, isPrimary: true);
            }

            if (channel.IsFirst)
            {
                var instance = new SingleInstance(channel, isPrimary: true);
                instance.StartListening();
                return instance;
            }

            bool answered;
            try
            {
                // The first round asks and waits the full answer time. Later rounds look for a late answer and repeat the
                // request once per answer time (the first instance may have been too busy to listen yet).
                var first = !asked;
                if (first || clock.Elapsed >= nextAsk)
                {
                    channel.SignalFirst();
                    nextAsk = clock.Elapsed + wait.AckTimeout;
                }

                asked = true;
                answered = channel.WaitAcknowledged(first ? wait.AckTimeout : TimeSpan.Zero);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not wake the running Murmur: {ex.Message}");
                answered = true;
            }
            finally
            {
                channel.Dispose();
            }

            if (answered || clock.Elapsed >= wait.RetryWindow)
            {
                return new SingleInstance(null, isPrimary: false);
            }

            Thread.Sleep(wait.RetryInterval);
        }
    }

    private static IInstanceChannel? TryOpen(Func<IInstanceChannel?> open)
    {
        try
        {
            return open();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Single-instance check unavailable: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    public void Acknowledge()
    {
        try
        {
            _channel?.Acknowledge();
        }
        catch (ObjectDisposedException)
        {
        }
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
    private readonly EventWaitHandle _ack;

    private WindowsInstanceChannel(Mutex mutex, EventWaitHandle signal, EventWaitHandle ack, bool isFirst)
    {
        _mutex = mutex;
        _event = signal;
        _ack = ack;
        IsFirst = isFirst;
    }

    public bool IsFirst { get; }

    /// <summary>Creates, or opens when it already exists, the mutex and the event.</summary>
    public static WindowsInstanceChannel Open()
    {
        var mutex = new Mutex(initiallyOwned: false, SingleInstance.MutexName, out var created);
        EventWaitHandle? signal = null;
        try
        {
            signal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, SingleInstance.EventName);
            var ack = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, SingleInstance.AckEventName);
            return new WindowsInstanceChannel(mutex, signal, ack, created);
        }
        catch
        {
            signal?.Dispose();
            mutex.Dispose();
            throw;
        }
    }

    public void SignalFirst()
    {
        // An answer nobody claimed (an earlier asker gave up) must not be taken for the answer to this request.
        _ack.Reset();
        _event.Set();
    }

    public void Acknowledge() => _ack.Set();

    public bool WaitAcknowledged(TimeSpan timeout) => _ack.WaitOne(timeout);

    public bool Wait(CancellationToken cancellationToken) =>
        WaitHandle.WaitAny([_event, cancellationToken.WaitHandle]) == 0;

    public void Dispose()
    {
        _ack.Dispose();
        _event.Dispose();
        _mutex.Dispose();
    }
}

/// <summary>How long a second start waits for the first one, and how it retries (the defaults are the product's; tests shorten them).</summary>
/// <param name="AckTimeout">How long the first answer is awaited, and the period at which the request is repeated afterwards.</param>
/// <param name="RetryInterval">How often a second start looks again whether it can become the first one.</param>
/// <param name="RetryWindow">When a second start whose first instance never goes away gives up (and exits).</param>
internal sealed record SingleInstanceTiming(TimeSpan AckTimeout, TimeSpan RetryInterval, TimeSpan RetryWindow)
{
    public static SingleInstanceTiming Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(15));
}
