namespace Murmur.Intelligence.Tests;

public sealed class IntelligenceAssemblyTests
{
    [Fact]
    public void MarkerName_MatchesAssemblyName()
    {
        Assert.Equal(IntelligenceAssembly.Name, typeof(IntelligenceAssembly).Assembly.GetName().Name);
    }
}
