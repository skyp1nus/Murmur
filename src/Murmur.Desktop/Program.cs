using Avalonia;

namespace Murmur.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var root = new CompositionRoot(args);
        return BuildAvaloniaApp(root).StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp(CompositionRoot root) =>
        AppBuilder.Configure(root.CreateApp)
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
