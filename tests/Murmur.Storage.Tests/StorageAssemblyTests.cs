namespace Murmur.Storage.Tests;

public sealed class StorageAssemblyTests
{
    [Fact]
    public void MarkerName_MatchesAssemblyName()
    {
        Assert.Equal(StorageAssembly.Name, typeof(StorageAssembly).Assembly.GetName().Name);
    }
}
