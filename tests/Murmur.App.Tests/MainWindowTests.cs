using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Murmur.App.ViewModels;
using Murmur.App.Views;

namespace Murmur.App.Tests;

public sealed class MainWindowTests
{
    [Fact]
    public async Task MainWindow_IsTitledMurmur_AndBindsStatus()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(MainWindowTests).Assembly);

        var (title, statusTexts) = await session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = new MainWindowViewModel { Status = "Hello" } };
                window.Show();
                var result = (window.Title, window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray());
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("Murmur", title);
        Assert.Contains("Hello", statusTexts);
    }
}
