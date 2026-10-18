using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Murmur.App.Controls;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

/// <summary>Builds screens on the headless platform with fake services.</summary>
internal sealed class UiHarness
{
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 15, 0, 0, TimeSpan.Zero);

    public FakeRecorder Recorder { get; } = new();

    public FakeCapture Capture { get; } = new();

    public FakeSessionStore Store { get; } = new();

    public TestTime Time { get; } = new(Now);

    public static HeadlessUnitTestSession Session =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(UiHarness).Assembly);

    public ShellViewModel CreateShell()
    {
        var library = new LibraryViewModel(Store, Time);
        var recording = new RecordingViewModel(Recorder, Capture, Store, action => action());
        return new ShellViewModel(library, recording);
    }

    /// <summary>Four typical sessions: today, yesterday, this week, older; one interrupted.</summary>
    public void SeedSessions()
    {
        Store.Add(Sessions.Make("Щотижневий стендап команди", Now.AddHours(-1), TimeSpan.FromMinutes(23) + TimeSpan.FromSeconds(41), screen: true));
        Store.Add(Sessions.Make("Ревʼю дизайну: нова бібліотека", Now.AddDays(-1).AddHours(-2), TimeSpan.FromMinutes(52) + TimeSpan.FromSeconds(9)));
        Store.Add(Sessions.Make("Інтервʼю з кандидатом", Now.AddDays(-4), TimeSpan.FromHours(1) + TimeSpan.FromMinutes(7), mic: true, system: true, screen: true));
        Store.Add(Sessions.Make("Дзвінок із клієнтом", Now.AddDays(-9), TimeSpan.FromMinutes(8) + TimeSpan.FromSeconds(30), mic: true, system: false, state: SessionState.Interrupted));
        Store.Add(Sessions.Make("Ретроспектива спринту", Now.AddDays(-21), TimeSpan.FromMinutes(45), mic: false, system: true));
    }

    public static MainWindow Show(ShellViewModel shell)
    {
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Pump();
        return window;
    }

    /// <summary>Runs pending UI jobs and a few render ticks (animation frames included).</summary>
    public static void Pump(int ticks = 3)
    {
        for (var i = 0; i < ticks; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    public static void Save(Window window, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(path, new PngBitmapEncoderOptions());
    }

    /// <summary>The rendered frame's pixels, row by row without padding, for exact comparisons.</summary>
    public static byte[] Pixels(Window window)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var buffer = frame.Lock();
        var rowLength = buffer.Size.Width * 4;
        var pixels = new byte[rowLength * buffer.Size.Height];
        for (var y = 0; y < buffer.Size.Height; y++)
        {
            Marshal.Copy(buffer.Address + (y * buffer.RowBytes), pixels, y * rowLength, rowLength);
        }

        return pixels;
    }

    /// <summary>
    /// Lets the recording orb breathe for 2.8 s on a manual clock. Setting the clock restarts the orb's animation, so
    /// whatever its real-time timer did while the window was being shown leaves no trace and every run renders the same
    /// frame.
    /// </summary>
    public static void BreatheOrb(OrbControl orb)
    {
        var clock = new TestTime(Now);
        orb.Clock = clock;
        orb.Tick();
        for (var i = 1; i <= 40; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(70));
            orb.Tick();
        }
    }
}
