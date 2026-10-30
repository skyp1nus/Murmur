using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Murmur.App.Appearance;
using Murmur.App.Diagnostics;
using Murmur.App.Localization;
using Murmur.App.Services;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Input;

namespace Murmur.App;

/// <summary>
/// Avalonia application. Services are passed in by the composition root in <c>Murmur.Desktop</c>;
/// the UI never constructs platform services itself.
/// </summary>
public partial class App : Application
{
    /// <summary>Parameterless constructor for the XAML loader and designer.</summary>
    public App()
        : this([])
    {
    }

    public App(IReadOnlyList<string> args)
        : this(args, null)
    {
    }

    /// <param name="args">Command-line arguments the process was started with.</param>
    /// <param name="services">Services from the composition root; <c>null</c> in designer and headless tests (no window is created).</param>
    public App(IReadOnlyList<string> args, AppServices? services)
    {
        ArgumentNullException.ThrowIfNull(args);
        Args = args;
        Services = services;
    }

    private ThemeApplier? _themeApplier;

    /// <summary>Services handed over by the composition root, if any.</summary>
    public AppServices? Services { get; }

    /// <summary>Command-line arguments the process was started with.</summary>
    public IReadOnlyList<string> Args { get; }

    public override void Initialize()
    {
        if (Services?.Settings is { } settings)
        {
            // The language is read once, before any text is looked up; a change applies on the next start (D-032).
            UiCulture.Apply(settings.Current.Language);
        }

        AvaloniaXamlLoader.Load(this);

        if (Services?.Settings is { } themeSettings)
        {
            _themeApplier = new ThemeApplier(this, themeSettings);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IControlledApplicationLifetime lifetime)
        {
            lifetime.Exit += (_, _) => _themeApplier?.Dispose();
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Services is { } services)
        {
            var shell = ShellViewModel.Create(services);
            var window = new MainWindow { DataContext = shell };

            // The players of an open session are released with the app, not left to the finalizers.
            if (ApplicationLifetime is IControlledApplicationLifetime exiting)
            {
                exiting.Exit += (_, _) => shell.CloseOpenSession();
            }

            if (services.Activation is { } activation)
            {
                // A second start of the program asks this one to show its window (raised off the UI thread). It is answered only
                // while the window lives and is not closing: otherwise the second start waits for this process to end.
                void OnActivationRequested() => Dispatcher.UIThread.Post(() =>
                {
                    if (window.IsClosed || window.IsClosing)
                    {
                        return;
                    }

                    window.BringToFront();
                    activation.Acknowledge();
                });
                activation.Requested += OnActivationRequested;
                window.Closed += (_, _) => activation.Requested -= OnActivationRequested;
            }

            if (services.Motion is { } motion)
            {
                // The orbs hold still while the system asks for less motion, and move again when it stops asking.
                MotionFollower.Attach(window, motion);
            }

            var probing = StartupProbe.TryGetPath(Args, out var probePath);
            if (probing)
            {
                StartupProbe.Attach(window, probePath, code => desktop.Shutdown(code));
            }

            var autoRecording = AutoRecordOptions.TryParse(Args, out var autoRecord, out _) && autoRecord is not null;
            if (services.Hotkeys is { } hotkeys && !probing && !autoRecording)
            {
                // Diagnostic runs (start-up probe, --autorecord) never register hotkeys: nothing may start a recording behind them.
                AttachHotkeys(window, shell, services, hotkeys);
            }

            if (autoRecording && autoRecord is not null)
            {
                window.Opened += (_, _) => _ = RunAutoRecordAsync(desktop, shell, services, autoRecord);
            }
            else
            {
                window.Opened += (_, _) => _ = shell.InitializeAsync();
            }

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The global hotkeys and "another start asked for the window" both bring the main window to the front. The hotkeys are
    /// registered after the window has rendered its first frame, not before, so they stay out of the start-up budget.
    /// </summary>
    private static void AttachHotkeys(MainWindow window, ShellViewModel shell, AppServices services, IGlobalHotkeys hotkeys)
    {
        if (services.Settings is not { } settings)
        {
            return;
        }

        var coordinator = new HotkeyCoordinator(hotkeys, settings, shell, window.BringToFront, isClosing: () => window.IsClosing);
        window.Opened += (_, _) => window.RequestAnimationFrame(_ => coordinator.Start());
        window.Closed += (_, _) => coordinator.Dispose();
    }

    private static async Task RunAutoRecordAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        ShellViewModel shell,
        AppServices services,
        AutoRecordOptions options)
    {
        var code = 1;
        try
        {
            await shell.InitializeAsync();
            shell.ShowRecording();
            var runner = new AutoRecordRunner(
                options,
                shell.Recording,
                services.Recorder,
                services.Sessions,
                action => Dispatcher.UIThread.InvokeAsync(action));
            code = await Task.Run(() => runner.RunAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"--autorecord failed: {ex.Message}");
        }

        Dispatcher.UIThread.Post(() => desktop.Shutdown(code));
    }
}
