using Murmur.Core.Settings;

namespace Murmur.Storage.Tests;

public sealed class AccelerationSettingTests
{
    [Fact]
    public void Default_IsCpu()
    {
        Assert.Equal(AiAcceleration.Cpu, new AppSettings().Acceleration);
    }

    [Theory]
    [InlineData(AiAcceleration.Cpu)]
    [InlineData(AiAcceleration.Vulkan)]
    [InlineData(AiAcceleration.Cuda)]
    public async Task Save_ThenReopen_RestoresTheChoice_WrittenAsAString(AiAcceleration choice)
    {
        using var root = new TempDataRoot();
        await new JsonSettingsStore(new StoragePaths(root.Path)).SaveAsync(new AppSettings { Acceleration = choice }, TestContext.Current.CancellationToken);

        var text = await File.ReadAllTextAsync(root.SettingsFile, TestContext.Current.CancellationToken);
        var reopened = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Contains($"\"acceleration\": \"{choice}\"", text, StringComparison.Ordinal);
        Assert.Equal(choice, reopened.Current.Acceleration);
    }

    [Fact]
    public void AnOldFileWithoutTheSetting_MeansCpu()
    {
        using var root = new TempDataRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(root.SettingsFile, """{ "theme": "Dark", "chatModelId": "gemma-4-e4b-q4" }""");

        var store = new JsonSettingsStore(new StoragePaths(root.Path));

        Assert.Equal(AiAcceleration.Cpu, store.Current.Acceleration);
        Assert.Equal("gemma-4-e4b-q4", store.Current.ChatModelId);
    }
}
