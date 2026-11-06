using Murmur.Core.Import;

namespace Murmur.Desktop.Tests;

public sealed class ImportCompositionTests
{
    /// <summary>This test project runs on the portable build (no WINDOWS symbol): the Downloads folder is ~/Downloads there.</summary>
    [Fact]
    public void CreateDownloadsFolder_OnThePortableBuild_IsTheHomeDownloadsFolder()
    {
        Assert.IsType<HomeDownloadsFolder>(CompositionRoot.CreateDownloadsFolder());
    }
}
