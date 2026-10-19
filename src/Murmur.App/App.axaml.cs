using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Murmur.App.Appearance;
using Murmur.App.Diagnostics;
using Murmur.App.Localization;
using Murmur.App.ViewModels;
using Murmur.App.Views;

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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Services is { } services)
        {
            var shell = ShellViewModel.Create(services);
            var window = new MainWindow { DataContext = shell };

            if (StartupProbe.TryGetPath(Args, out var probePath))
            {
                StartupProbe.Attach(window, probePath, code => desktop.Shutdown(code));
            }

            if (AutoRecordOptions.TryParse(Args, out var autoRecord, out _) && autoRecord is not null)
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
