using Avalonia;
using Murmur.App.Diagnostics;

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

        // One Murmur per user session: a second start wakes the first and ends here, before any UI. The mutex is held until
        // Main returns (declared first, so it is disposed last).
        using var instance = SingleInstance.Enter(args, SingleInstance.OpenSystemChannel);
        if (!instance.IsPrimary)
        {
            return 0;
        }

        using var root = new CompositionRoot(args, instance);
        return BuildAvaloniaApp(root).StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp(CompositionRoot root) =>
        AppBuilder.Configure(root.CreateApp)
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
