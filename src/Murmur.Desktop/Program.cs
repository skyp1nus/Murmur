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

        var code = Run(args, log, diagnostics);

        // Reached only when the app ended normally (an unhandled exception ends the process in its handler): the pending warnings go to
        // the disk, and a crash file of an earlier run counts as seen.
        log.MarkCleanExit();
        return code;
    }

    private static int Run(string[] args, FileAppLog log, DiagnosticContext diagnostics)
    {
        // One Murmur per user session: a second start wakes the first and ends here, before any UI. The mutex is held until
        // Run returns (declared first, so it is disposed last).
        using var instance = SingleInstance.Enter(args, SingleInstance.OpenSystemChannel, log: log);
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
