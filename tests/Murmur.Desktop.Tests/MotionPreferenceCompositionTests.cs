using Murmur.Core.Platform;

namespace Murmur.Desktop.Tests;

public sealed class MotionPreferenceCompositionTests
{
    /// <summary>This test project runs on the portable build (no WINDOWS symbol): motion is never reduced there.</summary>
    [Fact]
    public void CreateMotionPreference_OnThePortableBuild_IsTheFullMotionPreference()
    {
        var preference = CompositionRoot.CreateMotionPreference();

        Assert.IsType<FullMotionPreference>(preference);
        Assert.False(preference.ReduceMotion);
    }
}
