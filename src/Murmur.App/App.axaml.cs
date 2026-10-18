using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
    {
        ArgumentNullException.ThrowIfNull(args);
        Args = args;
    }

    /// <summary>Command-line arguments the process was started with.</summary>
    public IReadOnlyList<string> Args { get; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
