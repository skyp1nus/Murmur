using Murmur.Core.Calls;
using Murmur.Core.Settings;

namespace Murmur.Core.Tests.Calls;

public sealed class CallAppNamesTests
{
    [Theory]
    [InlineData("ms-teams", "Microsoft Teams")]
    [InlineData("MS-Teams.exe", "Microsoft Teams")]
    [InlineData("Teams", "Microsoft Teams")]
    [InlineData("Zoom", "Zoom")]
    [InlineData("zoom.exe", "Zoom")]
    [InlineData("chrome", "Google Chrome")]
    [InlineData("msedge", "Microsoft Edge")]
    [InlineData("firefox", "Firefox")]
    [InlineData("slack", "Slack")]
    [InlineData("Discord", "Discord")]
    [InlineData("webex", "Webex")]
    [InlineData("skype", "Skype")]
    public void KnownProcesses_HaveTheirFriendlyName(string process, string expected) =>
        Assert.Equal(expected, CallAppNames.FriendlyName(process));

    [Theory]
    [InlineData("SomeVoip", "SomeVoip")]
    [InlineData("  my-call.exe ", "my-call")]
    public void AnUnknownProcess_KeepsItsOwnName(string process, string expected) =>
        Assert.Equal(expected, CallAppNames.FriendlyName(process));

    [Fact]
    public void SameProcess_IgnoresCaseAndTheExeSuffix()
    {
        Assert.True(CallAppNames.SameProcess("Zoom.exe", "zoom"));
        Assert.False(CallAppNames.SameProcess("zoom", "slack"));
    }

    [Fact]
    public void TheNullMonitor_IsUnsupported_AndReportsNothing()
    {
        using var monitor = new NullAudioSessionMonitor();
        monitor.Changed += () => throw new InvalidOperationException("never raised");
        monitor.Start();

        Assert.False(monitor.IsSupported);
        Assert.False(monitor.Snapshot().MicrophoneInUse);
        Assert.Empty(monitor.Snapshot().Rendering);
        monitor.Stop();
    }
}

public sealed class CallSettingsTests
{
    [Fact]
    public void Defaults_DetectCalls_WithAnEmptyAllowlist()
    {
        var settings = new AppSettings();

        Assert.True(settings.DetectCalls);
        Assert.Empty(settings.CallAllowlist);
    }

    [Fact]
    public void TheAllowlist_IsNormalized_AndComparesByValue()
    {
        var a = new AppSettings { CallAllowlist = ["Zoom.exe", " ms-teams ", "zoom", "", null!] };
        var b = new AppSettings { CallAllowlist = new List<string> { "ZOOM", "MS-Teams" } };

        Assert.Equal(["Zoom", "ms-teams"], a.CallAllowlist);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { CallAllowlist = ["zoom"] });
        Assert.Equal(new AppSettings(), new AppSettings { CallAllowlist = new List<string>() });
        Assert.Empty(new AppSettings { CallAllowlist = null! }.CallAllowlist);
    }

    [Fact]
    public void AddAndRemove_AreCaseInsensitive()
    {
        var list = ProcessNameList.From(["Zoom"]);

        Assert.Same(list, list.Add("zoom.exe"));
        Assert.Equal(["Zoom", "slack"], list.Add("slack"));
        Assert.True(list.Contains("ZOOM.EXE"));
        Assert.False(list.Contains(null));
        Assert.Empty(list.Remove("zoom"));
        Assert.Same(list, list.Remove("slack"));
    }
}
