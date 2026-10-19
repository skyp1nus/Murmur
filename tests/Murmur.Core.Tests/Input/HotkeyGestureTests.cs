using Murmur.Core.Input;

namespace Murmur.Core.Tests.Input;

public sealed class HotkeyGestureTests
{
    public static TheoryData<string, HotkeyModifiers, HotkeyKey> Valid => new()
    {
        { "Ctrl+Shift+R", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, HotkeyKey.R },
        { "Ctrl+Alt+F9", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, HotkeyKey.F9 },
        { "Ctrl+D1", HotkeyModifiers.Ctrl, HotkeyKey.D1 },
        { "Alt+Space", HotkeyModifiers.Alt, HotkeyKey.Space },
        { "Cmd+Up", HotkeyModifiers.Meta, HotkeyKey.Up },
        { "Ctrl+Alt+Shift+Cmd+F24", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Meta, HotkeyKey.F24 },
        { "Alt+Shift+PageDown", HotkeyModifiers.Alt | HotkeyModifiers.Shift, HotkeyKey.PageDown },
        { "Ctrl+NumPad7", HotkeyModifiers.Ctrl, HotkeyKey.NumPad7 },
        { "Ctrl+PrintScreen", HotkeyModifiers.Ctrl, HotkeyKey.PrintScreen },
        { "Ctrl+Oem3", HotkeyModifiers.Ctrl, HotkeyKey.OemTilde },
        { "Ctrl+Alt+OemMinus", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, HotkeyKey.OemMinus },
        // Aliases and looser spelling.
        { "control+shift+r", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, HotkeyKey.R },
        { "CTRL + ALT + f9", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, HotkeyKey.F9 },
        { "  Ctrl\t+  Alt +F9 ", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, HotkeyKey.F9 },
        { "Win+E", HotkeyModifiers.Meta, HotkeyKey.E },
        { "Meta+E", HotkeyModifiers.Meta, HotkeyKey.E },
        { "Shift+Ctrl+A", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, HotkeyKey.A },
        { "A+Ctrl", HotkeyModifiers.Ctrl, HotkeyKey.A },
        { "Ctrl+OemTilde", HotkeyModifiers.Ctrl, HotkeyKey.OemTilde },
        { "Ctrl+Oem1", HotkeyModifiers.Ctrl, HotkeyKey.OemSemicolon },
        { "Ctrl+Snapshot", HotkeyModifiers.Ctrl, HotkeyKey.PrintScreen },
        { "Ctrl+Next", HotkeyModifiers.Ctrl, HotkeyKey.PageDown },
    };

    [Theory]
    [MemberData(nameof(Valid))]
    public void TryParse_AcceptsValidGestures(string text, HotkeyModifiers modifiers, HotkeyKey key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(new HotkeyGesture(modifiers, key), gesture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("R")] // no modifier would steal typing
    [InlineData("F9")]
    [InlineData("Shift+R")] // Shift alone would steal typing
    [InlineData("Ctrl")] // no key
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+A+B")] // two keys
    [InlineData("Ctrl+A+A")]
    [InlineData("Ctrl+Ctrl+A")] // duplicate modifier
    [InlineData("Ctrl+Control+A")] // duplicate under an alias
    [InlineData("Cmd+Win+A")]
    [InlineData("Ctrl+")]
    [InlineData("+A")]
    [InlineData("Ctrl++A")]
    [InlineData("Ctrl++")] // Avalonia reads this as OemPlus; the symbol spelling is not supported
    [InlineData("Ctrl+1")] // Avalonia reads a bare digit as something else: only D1 is the digit key
    [InlineData("Ctrl+Plus")]
    [InlineData("Ctrl+Enter")]
    [InlineData("Ctrl+Esc")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Super+A")]
    [InlineData("Ctrl A")]
    [InlineData("Ctrl-A")]
    [InlineData("Ctrl,A")]
    [InlineData("Ctrl+Shift+Alt+Cmd+Cmd+A")]
    public void TryParse_RefusesInvalidGestures(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(default, gesture);
    }

    [Fact]
    public void ToString_UsesCanonicalNamesAndAvaloniaOrder()
    {
        Assert.Equal("Ctrl+Shift+Alt+Cmd+R", new HotkeyGesture(
            HotkeyModifiers.Meta | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Ctrl, HotkeyKey.R).ToString());
        Assert.Equal("Ctrl+Alt+F9", new HotkeyGesture(HotkeyModifiers.Alt | HotkeyModifiers.Ctrl, HotkeyKey.F9).ToString());
        Assert.Equal("Ctrl+D1", new HotkeyGesture(HotkeyModifiers.Ctrl, HotkeyKey.D1).ToString());
        Assert.Equal("Alt+Oem3", new HotkeyGesture(HotkeyModifiers.Alt, HotkeyKey.OemTilde).ToString());
        Assert.Equal("Alt+Oem4", new HotkeyGesture(HotkeyModifiers.Alt, HotkeyKey.OemOpenBrackets).ToString());
    }

    [Fact]
    public void ToString_NormalisesAliases()
    {
        Assert.True(HotkeyGesture.TryParse("control + win + snapshot", out var gesture));
        Assert.Equal("Ctrl+Cmd+PrintScreen", gesture.ToString());
    }

    [Fact]
    public void EveryKey_RoundTrips_WithEveryModifierSet()
    {
        foreach (var key in Enum.GetValues<HotkeyKey>())
        {
            foreach (var modifiers in new[]
            {
                HotkeyModifiers.Ctrl,
                HotkeyModifiers.Alt,
                HotkeyModifiers.Meta,
                HotkeyModifiers.Ctrl | HotkeyModifiers.Shift,
                HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Meta,
            })
            {
                var gesture = new HotkeyGesture(modifiers, key);
                var text = gesture.ToString();

                Assert.True(HotkeyGesture.TryParse(text, out var parsed), text);
                Assert.Equal(gesture, parsed);
                Assert.Equal(text, parsed.ToString());
            }
        }
    }

    [Fact]
    public void KeyNames_AreUniqueAcrossKeys()
    {
        var names = Enum.GetValues<HotkeyKey>().Select(HotkeyGesture.KeyName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Keys_CoverTheSpecifiedSet()
    {
        var keys = Enum.GetValues<HotkeyKey>().Select(k => k.ToString()).ToHashSet();
        foreach (var expected in
                 "ABCDEFGHIJKLMNOPQRSTUVWXYZ".Select(c => c.ToString())
                     .Concat(Enumerable.Range(0, 10).Select(i => $"D{i}"))
                     .Concat(Enumerable.Range(1, 24).Select(i => $"F{i}"))
                     .Concat(Enumerable.Range(0, 10).Select(i => $"NumPad{i}"))
                     .Concat(["Space", "Insert", "Delete", "Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right", "Pause", "PrintScreen", "OemPlus", "OemMinus", "OemComma", "OemPeriod"]))
        {
            Assert.Contains(expected, keys);
        }
    }

    [Fact]
    public void Default_ToStringDoesNotThrow() => Assert.NotNull(default(HotkeyGesture).ToString());

    [Fact]
    public void UnsupportedGlobalHotkeys_ReportsUnsupported()
    {
        using var hotkeys = new UnsupportedGlobalHotkeys();
        hotkeys.Pressed += _ => { };

        Assert.False(hotkeys.IsSupported);
        Assert.Equal(HotkeyRegistration.Cleared, hotkeys.Register(HotkeyAction.ToggleRecording, null));
        Assert.Equal(HotkeyRegistration.Unsupported, hotkeys.Register(HotkeyAction.ToggleRecording, "Ctrl+Alt+R"));
        Assert.Equal(HotkeyRegistration.Unsupported, hotkeys.Register(HotkeyAction.ShowApp, "garbage"));
    }
}
