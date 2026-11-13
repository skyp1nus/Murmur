using Avalonia;
using Murmur.App.Diagnostics;
using Murmur.Storage;

namespace Murmur.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!AutoRecordOptions.TryParse(args, out _, out var error) && error.Length > 0)
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        // The diagnostic log comes first (#100): whatever fails after this leaves a trace. It writes nothing in a healthy run.
        var diagnostics = new DiagnosticContext();
        using var log = new FileAppLog(StoragePaths.FromEnvironment(), TimeProvider.System, diagnostics.Describe);
        ProcessCrashLog.Attach(log);

        var code = Run(args, log, diagnostics, out var primary);

        // Reached only when the process ended normally (an unhandled exception ends it in its handler).
        ProcessCrashLog.OnExit(log, args, primary);
        return code;
    }

    private static int Run(string[] args, FileAppLog log, DiagnosticContext diagnostics, out bool primary)
    {
        // One Murmur per user session: a second start wakes the first and ends here, before any UI. The mutex is held until
        // Run returns (declared first, so it is disposed last).
        using var instance = SingleInstance.Enter(args, SingleInstance.OpenSystemChannel, log: log);
        primary = instance.IsPrimary;
        if (!instance.IsPrimary)
        {
            return 0;
        }

        using var root = new CompositionRoot(args, instance, log, diagnostics);
        return BuildAvaloniaApp(root).StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp(CompositionRoot root) =>
        AppBuilder.Configure(root.CreateApp)
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
