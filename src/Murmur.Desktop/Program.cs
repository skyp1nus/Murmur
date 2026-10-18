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

        using var root = new CompositionRoot(args);
        return BuildAvaloniaApp(root).StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp(CompositionRoot root) =>
        AppBuilder.Configure(root.CreateApp)
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
