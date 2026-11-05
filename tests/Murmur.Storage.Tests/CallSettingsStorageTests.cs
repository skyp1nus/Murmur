using Murmur.Core.Settings;

namespace Murmur.Storage.Tests;

/// <summary>The call-detection settings of #44 in <c>settings.json</c> (source-generated JSON).</summary>
public sealed class CallSettingsStorageTests
{
    [Fact]
    public async Task DetectCallsAndTheAllowlist_SurviveARestart()
    {
        using var root = new TempDataRoot();
        var saved = new AppSettings { DetectCalls = false, CallAllowlist = ["ms-teams", "Zoom"] };

        await new JsonSettingsStore(new StoragePaths(root.Path)).SaveAsync(saved, TestContext.Current.CancellationToken);
        var reopened = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Equal(saved, reopened.Current);
        Assert.False(reopened.Current.DetectCalls);
        Assert.Equal(["ms-teams", "Zoom"], reopened.Current.CallAllowlist);
        var text = await File.ReadAllTextAsync(root.SettingsFile, TestContext.Current.CancellationToken);
        Assert.Contains("\"detectCalls\": false", text, StringComparison.Ordinal);
        Assert.Contains("\"callAllowlist\": [", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderFile_DetectsCalls_WithAnEmptyAllowlist_AndANullListReadsAsEmpty()
    {
        using var root = new TempDataRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.SettingsFile, """{ "settingsVersion": 2, "theme": "Dark", "callAllowlist": null }""");

        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.True(store.Current.DetectCalls);
        Assert.Empty(store.Current.CallAllowlist);
        Assert.Equal(new AppSettings { Theme = ThemePreference.Dark }, store.Current);
    }
}
