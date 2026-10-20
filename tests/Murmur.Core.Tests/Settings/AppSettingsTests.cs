using Murmur.Core.Input;
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

    [Fact]
    public void PauseHotkey_DefaultsToCtrlShiftP_AndIsAGlobalGesture()
    {
        var settings = new AppSettings();

        Assert.Equal("Ctrl+Shift+P", settings.PauseHotkey);
        Assert.True(HotkeyGesture.TryParse(settings.PauseHotkey, out var gesture));
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, HotkeyKey.P), gesture);
    }

    [Fact]
    public void EveryDefaultHotkey_IsDifferent()
    {
        var settings = new AppSettings();

        string?[] all = [settings.RecordHotkey, settings.ToggleScreenHotkey, settings.ShowAppHotkey, settings.PauseHotkey];
        Assert.Equal(all.Length, all.Distinct().Count());
    }
}
