using Murmur.Core.Settings;

namespace Murmur.Core.Tests.Settings;

public sealed class AppSettingsTests
{
    [Fact]
    public void Defaults_UseEnglish_AtSettingsVersion2()
    {
        var settings = new AppSettings();

        Assert.Equal("en", settings.Language);
        Assert.Equal(2, settings.SettingsVersion);
    }

    [Fact]
    public void ChangingOneValue_KeepsTheVersion()
    {
        var settings = new AppSettings() with { Language = "uk" };

        Assert.Equal("uk", settings.Language);
        Assert.Equal(2, settings.SettingsVersion);
    }
}
