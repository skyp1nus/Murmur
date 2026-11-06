using Murmur.Core.Settings;

namespace Murmur.Storage.Tests;

/// <summary>The "Follow along" choice of #56 in <c>settings.json</c> (source-generated JSON).</summary>
public sealed class FollowAlongSettingsStorageTests
{
    [Fact]
    public async Task FollowAlong_SurvivesARestart()
    {
        using var root = new TempDataRoot();
        var saved = new AppSettings { FollowAlong = false };

        await new JsonSettingsStore(new StoragePaths(root.Path)).SaveAsync(saved, TestContext.Current.CancellationToken);
        var reopened = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.False(reopened.Current.FollowAlong);
        var text = await File.ReadAllTextAsync(root.SettingsFile, TestContext.Current.CancellationToken);
        Assert.Contains("\"followAlong\": false", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderFile_FollowsAlong()
    {
        using var root = new TempDataRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.SettingsFile, """{ "settingsVersion": 2, "theme": "Light" }""");

        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.True(store.Current.FollowAlong);
    }
}
