using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Murmur.App.Tests.TestAppBuilder))]

namespace Murmur.App.Tests;

/// <summary>Headless Avalonia app for UI tests: the real <see cref="App"/> on the headless platform.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
