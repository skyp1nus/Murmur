using System.Globalization;

namespace Murmur.Desktop.Tests;

public sealed class StartupWarmUpTests
{
    [Fact]
    public async Task Begin_RunsOffTheCallingThread_AndCompletes()
    {
        var task = StartupWarmUp.Begin();

        await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(task.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("uk-UA")]
    [InlineData("tr-TR")]
    public void Run_LeavesCaseMappingUnchanged_ForAnyCulture(string name)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

            StartupWarmUp.Run();

            Assert.Equal("enter", "ENTER".ToLowerInvariant());
            Assert.Equal("ENTER", "enter".ToUpperInvariant());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
