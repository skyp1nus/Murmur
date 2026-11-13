using Murmur.Core.Settings;
using Murmur.Storage;

namespace Murmur.Storage.Tests;

/// <summary>The position of the floating orb (#46) survives a restart through the source-generated JSON.</summary>
public sealed class FloatingOrbPositionSettingTests
{
    [Fact]
    public void ByDefault_ThereIsNoSavedPosition()
    {
        Assert.Null(new AppSettings().FloatingOrbPosition);
    }

    [Fact]
    public async Task ASavedPosition_IsRestoredAfterReopening_AndWrittenAsCamelCase()
    {
        using var root = new TempDataRoot();
        var saved = new AppSettings { FloatingOrbPosition = new FloatingOrbPosition(1844.5, -312) };

        await new JsonSettingsStore(new StoragePaths(root.Path)).SaveAsync(saved, TestContext.Current.CancellationToken);
        var reopened = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Equal(new FloatingOrbPosition(1844.5, -312), reopened.Current.FloatingOrbPosition);
        var text = await File.ReadAllTextAsync(root.SettingsFile, TestContext.Current.CancellationToken);
        Assert.Contains("\"floatingOrbPosition\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileWithoutThePosition_LoadsWithTheDefault()
    {
        using var root = new TempDataRoot();
        Directory.CreateDirectory(root.Path);
        await File.WriteAllTextAsync(root.SettingsFile, "{ \"language\": \"uk\" }", TestContext.Current.CancellationToken);

        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Null(store.Current.FloatingOrbPosition);
    }
}
