namespace Murmur.Capture.Windows.Tests;

public sealed class CaptureWindowsAssemblyTests
{
    [Fact]
    [Trait("Category", "Windows")]
    public void Assembly_LoadsOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows capture tests run only on Windows.");

        Assert.Equal(CaptureWindowsAssembly.Name, typeof(CaptureWindowsAssembly).Assembly.GetName().Name);
    }
}
