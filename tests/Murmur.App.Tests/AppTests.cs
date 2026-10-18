using Avalonia.Headless;

namespace Murmur.App.Tests;

public sealed class AppTests
{
    private static HeadlessUnitTestSession Session =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AppTests).Assembly);

    [Fact]
    public async Task Constructor_WithArgs_ExposesThemViaArgs()
    {
        var args = await Session.Dispatch(
            () => new App(["--foo"]).Args,
            TestContext.Current.CancellationToken);

        Assert.Equal(["--foo"], args);
    }

    [Fact]
    public async Task ParameterlessConstructor_HasEmptyArgs()
    {
        var args = await Session.Dispatch(
            () => new App().Args,
            TestContext.Current.CancellationToken);

        Assert.Empty(args);
    }

    [Fact]
    public async Task Constructor_WithNullArgs_Throws()
    {
        var exception = await Session.Dispatch(
            () => Assert.Throws<ArgumentNullException>(() => new App(null!)),
            TestContext.Current.CancellationToken);

        Assert.Equal("args", exception.ParamName);
    }
}
