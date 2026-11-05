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
    public void MarkHotkey_DefaultsToCtrlAltM_AndIsAGlobalGesture_ShowAppKeepsCtrlShiftM()
    {
        var settings = new AppSettings();

        Assert.Equal("Ctrl+Alt+M", settings.MarkHotkey);
        Assert.True(HotkeyGesture.TryParse(settings.MarkHotkey, out var gesture));
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, HotkeyKey.M), gesture);
        Assert.Equal("Ctrl+Shift+M", settings.ShowAppHotkey);
    }

    [Fact]
    public void EveryDefaultHotkey_IsDifferent()
    {
        var settings = new AppSettings();

        string?[] all = [settings.RecordHotkey, settings.ToggleScreenHotkey, settings.ShowAppHotkey, settings.PauseHotkey, settings.MarkHotkey];
        Assert.Equal(all.Length, all.Distinct().Count());
    }
}
