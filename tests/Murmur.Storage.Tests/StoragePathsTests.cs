namespace Murmur.Storage.Tests;

public sealed class StoragePathsTests
{
    [Fact]
    public void EnvironmentOverride_IsUsedAsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "murmur-override");

        var paths = StoragePaths.Resolve(root, "/ignored");

        Assert.Equal(Path.GetFullPath(root), paths.Root);
        Assert.Equal(Path.Combine(paths.Root, "Recordings"), paths.RecordingsDirectory);
        Assert.Equal(Path.Combine(paths.Root, "murmur.db"), paths.DatabasePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoOverride_FallsBackToLocalAppDataMurmur(string? value)
    {
        var local = Path.Combine(Path.GetTempPath(), "local-app-data");

        var paths = StoragePaths.Resolve(value, local);

        Assert.Equal(Path.GetFullPath(Path.Combine(local, "Murmur")), paths.Root);
    }

    [Fact]
    public void FromEnvironment_HonoursMurmurDataDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "murmur-env-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable(StoragePaths.DataDirEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(StoragePaths.DataDirEnvironmentVariable, root);

            Assert.Equal(Path.GetFullPath(root), StoragePaths.FromEnvironment().Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StoragePaths.DataDirEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void FromEnvironment_WithoutOverride_EndsWithMurmur()
    {
        var previous = Environment.GetEnvironmentVariable(StoragePaths.DataDirEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(StoragePaths.DataDirEnvironmentVariable, null);

            var root = StoragePaths.FromEnvironment().Root;

            Assert.Equal("Murmur", Path.GetFileName(root));
        }
        finally
        {
            Environment.SetEnvironmentVariable(StoragePaths.DataDirEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void EmptyRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() => new StoragePaths(""));
    }
}
